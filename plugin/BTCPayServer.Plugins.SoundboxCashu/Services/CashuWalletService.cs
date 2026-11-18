using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SoundboxCashu.Cashu;
using BTCPayServer.Plugins.SoundboxCashu.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SoundboxCashu.Services;

/// <summary>
/// The store's Cashu wallet. Three jobs:
///
///  1. RECEIVE  a customer's ecash: verify it is for our mint, then swap it at
///              the mint for fresh proofs only this server knows. The swap is
///              what makes the payment final -- the customer's proofs are
///              spent, so they cannot be spent again elsewhere.
///  2. SETTLE   held ecash to the store's own Lightning node: create a normal
///              BTCPay invoice and have the mint pay it (a "melt"). BTCPay sees
///              an ordinary Lightning payment, so everything downstream --
///              the Soundbox announcement, webhooks, accounting -- just works,
///              and the merchant ends up holding sats on their own node rather
///              than IOUs at a mint.
///  3. RECOVER  anything interrupted half-way, from what was written to the
///              database before each mint call.
///
/// Money-safety rule followed throughout: anything the mint might sign is
/// persisted BEFORE the request is sent, and local state is only rolled back
/// when the mint explicitly refused (<see cref="CashuMintException"/>). A
/// timeout or dropped connection leaves state as-is for recovery to resolve.
/// </summary>
public class CashuWalletService
{
    private readonly SoundboxCashuDbContextFactory _dbFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IStoreInvoiceCreator _invoices;
    private readonly ILogger<CashuWalletService> _logger;

    // One settlement at a time per store, so two melts can never pick the
    // same proofs. (Receives don't need this: a swap only creates proofs.)
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _storeLocks = new();

    private readonly Channel<string> _settleQueue = Channel.CreateUnbounded<string>();

    // Most recent (invoice amount, fee reserve) each mint quoted, used to
    // size the next invoice. In-memory only: losing it on restart just means
    // one sizing round-trip.
    private static readonly ConcurrentDictionary<string, (long Amount, long Reserve)> LastReserve = new();

    public CashuWalletService(
        SoundboxCashuDbContextFactory dbFactory,
        IHttpClientFactory httpFactory,
        IStoreInvoiceCreator invoices,
        ILogger<CashuWalletService> logger)
    {
        _dbFactory = dbFactory;
        _httpFactory = httpFactory;
        _invoices = invoices;
        _logger = logger;
    }

    /// <summary>Store ids with ecash waiting to be settled. Drained by the worker.</summary>
    public ChannelReader<string> SettleRequests => _settleQueue.Reader;

    public void RequestSettlement(string storeId) => _settleQueue.Writer.TryWrite(storeId);

    public CashuMintClient Mint(string mintUrl) =>
        new(_httpFactory.CreateClient(nameof(SoundboxCashu)), mintUrl);

    // ───────────────────────────── Payment request ─────────────────────────

    public static string PayEndpoint(CashuStoreSettings s) =>
        $"{s.ServerUrl.TrimEnd('/')}/plugins/sbcashu/{s.StoreId}/pay";

    /// <summary>
    /// The NUT-18 request the soundbox displays. Amountless: like the LNURL
    /// QR it replaces or sits beside, the customer types what they owe.
    /// Strict mint list, so wallets only offer ecash we can actually redeem.
    /// </summary>
    public static string BuildPaymentRequest(CashuStoreSettings s) =>
        CashuEncoding.EncodePaymentRequest(new CashuPaymentRequest
        {
            Unit = SoundboxCashuConstants.Unit,
            Mints = [s.MintUrl],
            Transports = [new CashuTransport("post", PayEndpoint(s))]
        });

    // ─────────────────────────────── Receive ───────────────────────────────

    public record ReceiveResult(bool Success, int HttpStatus, string Message, long NetAmount = 0);

    public async Task<ReceiveResult> ReceiveAsync(string storeId, CashuPaymentPayload payload, CancellationToken ct)
    {
        await using var db = _dbFactory.CreateContext();
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.StoreId == storeId, ct);
        if (settings is null || !settings.Enabled)
            return new(false, 404, "This store does not accept Cashu payments.");

        // ── Validate before touching the mint ──
        if (payload.Mint is null || CashuMintClient.NormalizeMintUrl(payload.Mint) != settings.MintUrl)
            return new(false, 400, $"This store only accepts ecash from {settings.MintUrl}.");
        if (!string.Equals(payload.Unit ?? SoundboxCashuConstants.Unit, SoundboxCashuConstants.Unit, StringComparison.OrdinalIgnoreCase))
            return new(false, 400, "Only sat-denominated ecash is accepted.");

        var proofs = payload.Proofs ?? [];
        if (proofs.Count == 0)
            return new(false, 400, "The payment contains no ecash.");
        if (proofs.Count > SoundboxCashuConstants.MaxProofsPerPayment)
            return new(false, 400, $"Too many proofs in one payment (max {SoundboxCashuConstants.MaxProofsPerPayment}).");
        if (proofs.Any(p => p.Amount <= 0 || p.Amount > 1L << 40 || string.IsNullOrEmpty(p.Secret) ||
                            p.Secret.Length > 1024 || p.C.Length != 66 || string.IsNullOrEmpty(p.Id)))
            return new(false, 400, "The payment contains a malformed proof.");
        if (proofs.Select(p => p.Secret).Distinct().Count() != proofs.Count)
            return new(false, 400, "The payment contains the same proof twice.");

        var mint = Mint(settings.MintUrl);
        List<CashuKeysetInfo> keysets;
        try
        {
            keysets = await mint.GetKeysetsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Cashu: mint {Mint} unreachable while receiving", settings.MintUrl);
            return new(false, 503, "The mint is unreachable right now. Your ecash was not spent; please try again.");
        }

        var satKeysets = keysets.Where(k => k.Unit == SoundboxCashuConstants.Unit).ToDictionary(k => k.Id, k => k.InputFeePpk ?? 0);
        if (proofs.Any(p => !satKeysets.ContainsKey(p.Id)))
            return new(false, 400, "The ecash is not from a sat keyset of this mint.");

        var total = proofs.Sum(p => p.Amount);
        var fee = CashuMintClient.InputFee(proofs.Select(p => p.Id), satKeysets);
        var net = total - fee;
        if (net < 1)
            return new(false, 400, $"Payment of {total} sat does not cover the mint's {fee} sat fee.");

        var active = CashuWalletOps.PickActiveKeyset(keysets, SoundboxCashuConstants.Unit);
        if (active is null)
            return new(false, 503, "The mint has no active sat keyset.");
        Dictionary<long, string> keys;
        try
        {
            keys = await mint.GetKeysAsync(active.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Cashu: could not load keys from {Mint}", settings.MintUrl);
            return new(false, 503, "The mint is unreachable right now. Your ecash was not spent; please try again.");
        }
        var outputs = CashuWalletOps.PrepareOutputs(active.Id, CashuMintClient.SplitAmount(net, keys.Keys));

        // ── Persist intent before the swap ──
        var op = new CashuOperation
        {
            Id = NewId(),
            StoreId = storeId,
            Kind = CashuOperationKind.Receive,
            State = CashuOperationState.Swapping,
            MintUrl = settings.MintUrl,
            Amount = total,
            ReceiveFee = fee
        };
        db.Operations.Add(op);
        var rows = outputs.Select(o => PendingRow(storeId, settings.MintUrl, o, op.Id)).ToList();
        db.Proofs.AddRange(rows);
        await db.SaveChangesAsync(ct);

        // Strip everything but what the mint needs. DLEQ proofs in particular
        // are for the receiver to check, not for the swap.
        var inputs = proofs.Select(p => new CashuProof
        {
            Amount = p.Amount, Id = p.Id, Secret = p.Secret, C = p.C, Witness = p.Witness
        }).ToList();

        try
        {
            var sigs = await mint.SwapAsync(inputs, outputs.Select(o => o.ToBlindedMessage()).ToList(), ct);
            ApplySignatures(rows, outputs, sigs, keys);
            op.State = CashuOperationState.Received;
            op.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (CashuMintException ex)
        {
            // Refused: the customer's ecash is untouched and nothing was
            // signed for us. Safe to forget the outputs.
            db.Proofs.RemoveRange(rows);
            op.State = CashuOperationState.Failed;
            op.LastError = Truncate(ex.Message);
            op.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);

            _logger.LogInformation("Cashu: mint refused payment for store {Store}: {Error}", storeId, ex.Message);
            return new(false, 400, ex.IsAlreadySpent
                ? "This ecash has already been spent."
                : $"The mint rejected the ecash: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Unknown outcome. Leave the operation in Swapping: recovery will
            // ask the mint (NUT-09) whether it signed our outputs.
            _logger.LogWarning(ex, "Cashu: swap outcome unknown for operation {Op}", op.Id);
            return new(false, 502,
                "Could not confirm the payment with the mint. If your wallet shows this ecash as spent, " +
                "the payment went through and the merchant will receive it shortly.");
        }

        _logger.LogInformation("Cashu: received {Net} sat ({Total} - {Fee} fee) for store {Store}, op {Op}",
            net, total, fee, storeId, op.Id);
        RequestSettlement(storeId);
        return new(true, 200, "Payment received.", net);
    }

    // ─────────────────────────────── Settle ────────────────────────────────

    /// <summary>
    /// Pays out every Received operation of a store to its Lightning node,
    /// oldest first. Safe to call repeatedly and concurrently.
    /// </summary>
    public async Task SettleStoreAsync(string storeId, CancellationToken ct)
    {
        var gate = _storeLocks.GetOrAdd(storeId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            List<string> pending;
            await using (var db = _dbFactory.CreateContext())
            {
                pending = await db.Operations
                    .Where(o => o.StoreId == storeId && o.Kind == CashuOperationKind.Receive &&
                                o.State == CashuOperationState.Received)
                    .OrderBy(o => o.CreatedAt)
                    .Select(o => o.Id)
                    .ToListAsync(ct);
            }

            foreach (var opId in pending)
            {
                // Stop at the first failure: later operations would fail the
                // same way (the node is down, the mint can't route), and
                // trying each one would just create a pile of unpaid invoices.
                if (!await SettleOneAsync(opId, ct)) break;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> SettleOneAsync(string opId, CancellationToken ct)
    {
        await using var db = _dbFactory.CreateContext();
        var op = await db.Operations.FirstAsync(o => o.Id == opId, ct);
        if (op.State != CashuOperationState.Received) return true;

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.StoreId == op.StoreId, ct);
        if (settings is null || string.IsNullOrEmpty(settings.ServerUrl))
        {
            await NoteFailure(db, op, "Store settings missing; open the Soundbox Cashu page and save them.");
            return false;
        }

        var mint = Mint(op.MintUrl);
        var target = op.Amount - op.ReceiveFee;

        try
        {
            var keysets = await mint.GetKeysetsAsync(ct);
            var fees = keysets.ToDictionary(k => k.Id, k => k.InputFeePpk ?? 0);
            var active = CashuWalletOps.PickActiveKeyset(keysets, SoundboxCashuConstants.Unit)
                         ?? throw new InvalidOperationException("Mint has no active sat keyset");

            // The mint only reveals its Lightning fee reserve in a quote, and
            // a quote needs an invoice. To avoid creating an invoice that is
            // then abandoned (it would sit expired in the merchant's invoice
            // list), size the first one from the reserve this mint asked for
            // last time. A wrong estimate costs one extra invoice, not money.
            var amount = target;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var pool = await db.Proofs
                    .Where(p => p.StoreId == op.StoreId && p.MintUrl == op.MintUrl && p.State == CashuProofState.Unspent)
                    .ToListAsync(ct);

                if (attempt == 0 && LastReserve.TryGetValue(op.MintUrl, out var last))
                {
                    var affordableNow = pool.Sum(p => p.Amount) - CashuMintClient.InputFee(pool.Select(p => p.KeysetId), fees);
                    // Reserves are usually a percentage with a small floor,
                    // so scale proportionally but never below the floor seen.
                    var estimate = Math.Max(
                        (long)Math.Ceiling((double)last.Reserve * amount / Math.Max(1, last.Amount)),
                        Math.Min(last.Reserve, 2));
                    amount = Math.Clamp(affordableNow - estimate, 1, target);
                }

                var invoice = await _invoices.CreateLightningInvoiceAsync(
                    op.StoreId, settings.ServerUrl, amount, $"Cashu ecash payment {op.Id}", ct);
                var quote = await mint.CreateMeltQuoteAsync(invoice.Bolt11, ct);

                // Never pay an invoice for an amount other than the one we
                // asked BTCPay for.
                if (quote.Amount != amount)
                    throw new InvalidOperationException($"Mint quoted {quote.Amount} sat for a {amount} sat invoice");

                LastReserve[op.MintUrl] = (quote.Amount, quote.FeeReserve);

                var need = quote.Amount + quote.FeeReserve;
                var selected = CashuWalletOps.SelectProofs(pool, p => p.Amount, p => p.KeysetId, need, fees);
                if (selected is null)
                {
                    var affordable = pool.Sum(p => p.Amount) - CashuMintClient.InputFee(pool.Select(p => p.KeysetId), fees);
                    var reduced = Math.Min(amount - 1, affordable - quote.FeeReserve);
                    if (reduced < 1)
                    {
                        await NoteFailure(db, op,
                            $"Held ecash ({affordable} sat) cannot cover the Lightning fee reserve ({quote.FeeReserve} sat).");
                        return false;
                    }
                    amount = reduced;
                    continue;
                }

                await MeltAsync(db, mint, op, invoice.InvoiceId, quote, selected, active.Id, fees, ct);
                return op.State == CashuOperationState.Settled;
            }

            await NoteFailure(db, op, "Could not size an invoice the held ecash can pay.");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Cashu: settlement of {Op} failed", op.Id);
            // Only Received ops get here with state untouched; a melt in
            // flight keeps its Settling state for recovery.
            if (op.State == CashuOperationState.Received)
                await NoteFailure(db, op, ex.Message);
            return false;
        }
    }

    private async Task MeltAsync(
        SoundboxCashuDbContext db, CashuMintClient mint, CashuOperation op, string invoiceId,
        CashuMeltQuote quote, List<CashuProofRecord> selected, string keysetId,
        IReadOnlyDictionary<string, long> fees, CancellationToken ct)
    {
        var inputFee = CashuMintClient.InputFee(selected.Select(p => p.KeysetId), fees);
        var maxChange = selected.Sum(p => p.Amount) - inputFee - quote.Amount;
        var blanks = CashuWalletOps.PrepareBlankOutputs(keysetId, CashuMintClient.BlankOutputCount(maxChange));

        // ── Persist intent ──
        foreach (var p in selected)
        {
            p.State = CashuProofState.Reserved;
            p.SpentByOperationId = op.Id;
        }
        var blankRows = blanks.Select(b => PendingRow(op.StoreId, op.MintUrl, b, op.Id)).ToList();
        db.Proofs.AddRange(blankRows);
        op.State = CashuOperationState.Settling;
        op.InvoiceId = invoiceId;
        op.MeltQuoteId = quote.Quote;
        op.SettledAmount = quote.Amount;
        op.Attempts++;
        op.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        CashuMeltQuote result;
        try
        {
            result = await mint.MeltAsync(quote.Quote, selected.Select(ToProof).ToList(),
                blanks.Select(b => b.ToBlindedMessage()).ToList(), ct);
        }
        catch (CashuMintException ex)
        {
            // Refused outright: the inputs were not spent.
            await ReleaseAsync(db, op, selected, blankRows, ex.Message);
            return;
        }
        // Any other exception propagates with the op still Settling; the
        // recovery pass resolves it from the quote's state.

        await ApplyMeltResultAsync(db, mint, op, selected, blankRows, blanks, result, ct);
    }

    private async Task ApplyMeltResultAsync(
        SoundboxCashuDbContext db, CashuMintClient mint, CashuOperation op,
        List<CashuProofRecord> inputs, List<CashuProofRecord> blankRows, List<PreparedOutput> blanks,
        CashuMeltQuote result, CancellationToken ct)
    {
        switch (result.State)
        {
            case "PAID":
                foreach (var p in inputs) p.State = CashuProofState.Spent;

                var change = result.Change ?? [];
                if (change.Count > 0)
                {
                    var keys = await mint.GetKeysAsync(change[0].Id, ct);
                    // NUT-08: signatures come back in the order of the blank
                    // outputs, with zero-value ones omitted from the end.
                    for (var i = 0; i < change.Count && i < blanks.Count; i++)
                    {
                        var proof = CashuWalletOps.Unblind(blanks[i], change[i], keys);
                        blankRows[i].Amount = proof.Amount;
                        blankRows[i].C = proof.C;
                        blankRows[i].State = CashuProofState.Unspent;
                    }
                }
                db.Proofs.RemoveRange(blankRows.Where(r => r.State == CashuProofState.Pending));

                op.State = CashuOperationState.Settled;
                op.LastError = null;
                op.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                _logger.LogInformation("Cashu: op {Op} settled {Amount} sat to invoice {Invoice}",
                    op.Id, op.SettledAmount, op.InvoiceId);
                break;

            case "UNPAID":
                await ReleaseAsync(db, op, inputs, blankRows, "Mint could not pay the Lightning invoice");
                break;

            default:
                // PENDING: the Lightning payment is in flight. Recovery will
                // pick it up once the mint reports a final state.
                _logger.LogInformation("Cashu: op {Op} melt is {State}; will check again", op.Id, result.State);
                break;
        }
    }

    /// <summary>Undoes a melt that definitely did not happen.</summary>
    private static async Task ReleaseAsync(
        SoundboxCashuDbContext db, CashuOperation op, List<CashuProofRecord> inputs,
        List<CashuProofRecord> blankRows, string reason)
    {
        foreach (var p in inputs)
        {
            p.State = CashuProofState.Unspent;
            p.SpentByOperationId = null;
        }
        db.Proofs.RemoveRange(blankRows);
        op.State = CashuOperationState.Received;
        op.MeltQuoteId = null;
        op.InvoiceId = null;
        op.SettledAmount = null;
        op.LastError = Truncate(reason);
        op.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static async Task NoteFailure(SoundboxCashuDbContext db, CashuOperation op, string error)
    {
        op.LastError = Truncate(error);
        op.Attempts++;
        op.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Received operations whose last attempt has backed off long enough to
    /// be retried: 1, 2, 4 ... minutes, capped at an hour.
    /// </summary>
    public async Task<List<string>> StoresDueForSettlementAsync(CancellationToken ct)
    {
        await using var db = _dbFactory.CreateContext();
        var waiting = await db.Operations
            .Where(o => o.Kind == CashuOperationKind.Receive && o.State == CashuOperationState.Received)
            .Select(o => new { o.StoreId, o.Attempts, o.UpdatedAt })
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        return waiting
            .Where(o => o.Attempts == 0 ||
                        now - o.UpdatedAt >= TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(o.Attempts - 1, 6)))))
            .Select(o => o.StoreId)
            .Distinct()
            .ToList();
    }

    // ─────────────────────────────── Recover ───────────────────────────────

    /// <summary>
    /// Resolves operations interrupted mid-flight. Only touches operations
    /// older than <see cref="SoundboxCashuConstants.InterruptedAfter"/>, so it
    /// never races a request that is still running.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - SoundboxCashuConstants.InterruptedAfter;
        List<CashuOperation> stuck;
        await using (var db = _dbFactory.CreateContext())
        {
            stuck = await db.Operations
                .Where(o => (o.State == CashuOperationState.Swapping || o.State == CashuOperationState.Settling) &&
                            o.UpdatedAt < cutoff)
                .AsNoTracking()
                .ToListAsync(ct);
        }

        foreach (var op in stuck)
        {
            try
            {
                if (op.State == CashuOperationState.Swapping) await RecoverSwapAsync(op.Id, ct);
                else await RecoverMeltAsync(op.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Cashu: recovery of {Op} failed; will retry", op.Id);
            }
        }
    }

    private async Task RecoverSwapAsync(string opId, CancellationToken ct)
    {
        await using var db = _dbFactory.CreateContext();
        var op = await db.Operations.FirstAsync(o => o.Id == opId, ct);
        var pending = await db.Proofs
            .Where(p => p.CreatedByOperationId == op.Id && p.State == CashuProofState.Pending)
            .ToListAsync(ct);

        var restoredAny = await RestoreAsync(db, Mint(op.MintUrl), pending, ct);

        if (restoredAny)
        {
            // The swap went through after all: the customer paid.
            op.State = CashuOperationState.Received;
            op.LastError = null;
            _logger.LogInformation("Cashu: recovered interrupted receive {Op}", op.Id);
        }
        else
        {
            // The mint never signed: the swap did not happen, and the
            // customer still holds their ecash.
            db.Proofs.RemoveRange(pending);
            op.State = CashuOperationState.Failed;
            op.LastError = "The mint never completed the swap.";
        }
        op.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);

        if (restoredAny) RequestSettlement(op.StoreId);
    }

    private async Task RecoverMeltAsync(string opId, CancellationToken ct)
    {
        var gate = _storeLocks.GetOrAdd((await GetStoreIdAsync(opId, ct))!, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var db = _dbFactory.CreateContext();
            var op = await db.Operations.FirstAsync(o => o.Id == opId, ct);
            if (op.State != CashuOperationState.Settling) return;

            var mint = Mint(op.MintUrl);
            var inputs = await db.Proofs.Where(p => p.SpentByOperationId == op.Id && p.State == CashuProofState.Reserved).ToListAsync(ct);
            var blanks = await db.Proofs.Where(p => p.CreatedByOperationId == op.Id && p.State == CashuProofState.Pending).ToListAsync(ct);

            string? state = null;
            if (op.MeltQuoteId is not null)
            {
                try { state = (await mint.GetMeltQuoteAsync(op.MeltQuoteId, ct)).State; }
                catch (CashuMintException) { state = null; } // unknown quote: fall back to proof states
            }

            if (state == "PAID")
            {
                foreach (var p in inputs) p.State = CashuProofState.Spent;
                await RestoreAsync(db, mint, blanks, ct);
                db.Proofs.RemoveRange(blanks.Where(b => b.State == CashuProofState.Pending));
                op.State = CashuOperationState.Settled;
                op.LastError = null;
                op.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                _logger.LogInformation("Cashu: recovered settled melt {Op}", op.Id);
                return;
            }
            if (state == "PENDING") return;

            // UNPAID or unknown: trust the proofs themselves. Only if every
            // input is still UNSPENT is it safe to put them back in the pool.
            var states = await mint.CheckStateAsync(inputs.Select(p => CashuWalletOps.Y(p.Secret)).ToList(), ct);
            if (states.Count == inputs.Count && states.All(s => s.State == "UNSPENT"))
            {
                await ReleaseAsync(db, op, inputs, blanks, "Lightning payment did not complete; will retry.");
                RequestSettlement(op.StoreId);
            }
            else
            {
                _logger.LogWarning("Cashu: melt {Op} inputs are in mixed states; leaving for the next pass", op.Id);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string?> GetStoreIdAsync(string opId, CancellationToken ct)
    {
        await using var db = _dbFactory.CreateContext();
        return await db.Operations.Where(o => o.Id == opId).Select(o => o.StoreId).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// NUT-09: fills in any pending rows the mint did sign. Returns whether
    /// at least one was recovered.
    /// </summary>
    private static async Task<bool> RestoreAsync(
        SoundboxCashuDbContext db, CashuMintClient mint, List<CashuProofRecord> pending, CancellationToken ct)
    {
        if (pending.Count == 0) return false;

        var restored = await mint.RestoreAsync(pending.Select(p => new CashuBlindedMessage
        {
            Amount = p.Amount, Id = p.KeysetId, B_ = p.BlindedMessage
        }).ToList(), ct);

        var any = false;
        for (var i = 0; i < restored.Signatures.Count && i < restored.Outputs.Count; i++)
        {
            var row = pending.FirstOrDefault(p => p.BlindedMessage == restored.Outputs[i].B_);
            if (row is null) continue;
            var sig = restored.Signatures[i];
            var keys = await mint.GetKeysAsync(sig.Id, ct);
            var proof = CashuWalletOps.Unblind(ToPrepared(row), sig, keys);
            row.Amount = proof.Amount;
            row.C = proof.C;
            row.State = CashuProofState.Unspent;
            any = true;
        }
        await db.SaveChangesAsync(CancellationToken.None);
        return any;
    }

    // ─────────────────────────────── Export ────────────────────────────────

    /// <summary>
    /// Hands every spendable proof at one mint to the merchant as a token,
    /// for when they want their ecash rather than a Lightning settlement --
    /// or for the case where Lightning settlement keeps failing.
    /// Operations still waiting to settle at that mint are closed, since the
    /// ecash they were waiting on is now in the token.
    /// </summary>
    public async Task<CashuOperation?> ExportAsync(string storeId, string mintUrl, CancellationToken ct)
    {
        var gate = _storeLocks.GetOrAdd(storeId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var db = _dbFactory.CreateContext();
            var proofs = await db.Proofs
                .Where(p => p.StoreId == storeId && p.MintUrl == mintUrl && p.State == CashuProofState.Unspent)
                .ToListAsync(ct);
            if (proofs.Count == 0) return null;

            var op = new CashuOperation
            {
                Id = NewId(),
                StoreId = storeId,
                Kind = CashuOperationKind.Export,
                State = CashuOperationState.Exported,
                MintUrl = mintUrl,
                Amount = proofs.Sum(p => p.Amount),
                Token = CashuEncoding.EncodeTokenV4(mintUrl, SoundboxCashuConstants.Unit, proofs.Select(ToProof),
                    memo: "Soundbox Cashu export")
            };
            db.Operations.Add(op);

            foreach (var p in proofs)
            {
                p.State = CashuProofState.Spent;
                p.SpentByOperationId = op.Id;
            }

            var waiting = await db.Operations
                .Where(o => o.StoreId == storeId && o.MintUrl == mintUrl &&
                            o.Kind == CashuOperationKind.Receive && o.State == CashuOperationState.Received)
                .ToListAsync(ct);
            foreach (var w in waiting)
            {
                w.State = CashuOperationState.SweptToToken;
                w.LastError = null;
                w.UpdatedAt = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync(ct);
            return op;
        }
        finally
        {
            gate.Release();
        }
    }

    // ─────────────────────────────── Helpers ───────────────────────────────

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static CashuProofRecord PendingRow(string storeId, string mintUrl, PreparedOutput o, string opId) => new()
    {
        StoreId = storeId,
        MintUrl = mintUrl,
        KeysetId = o.KeysetId,
        Amount = o.Amount,
        Secret = o.Secret,
        BlindingFactor = o.BlindingFactor,
        BlindedMessage = o.B_,
        State = CashuProofState.Pending,
        CreatedByOperationId = opId
    };

    private static PreparedOutput ToPrepared(CashuProofRecord r) => new()
    {
        KeysetId = r.KeysetId,
        Amount = r.Amount,
        Secret = r.Secret,
        BlindingFactor = r.BlindingFactor,
        B_ = r.BlindedMessage
    };

    private static CashuProof ToProof(CashuProofRecord r) => new()
    {
        Amount = r.Amount, Id = r.KeysetId, Secret = r.Secret, C = r.C!
    };

    private static void ApplySignatures(
        List<CashuProofRecord> rows, List<PreparedOutput> outputs, List<CashuBlindSignature> sigs,
        IReadOnlyDictionary<long, string> keys)
    {
        if (sigs.Count != outputs.Count)
            throw new InvalidOperationException($"Mint returned {sigs.Count} signatures for {outputs.Count} outputs");
        for (var i = 0; i < outputs.Count; i++)
        {
            var proof = CashuWalletOps.Unblind(outputs[i], sigs[i], keys);
            rows[i].C = proof.C;
            rows[i].State = CashuProofState.Unspent;
        }
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];
}
