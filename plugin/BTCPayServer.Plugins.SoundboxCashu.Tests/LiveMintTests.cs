using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SoundboxCashu.Cashu;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.SoundboxCashu.Tests;

/// <summary>
/// End-to-end against a real Cashu mint: the public test mint run by the
/// Cashu project, which settles its own Lightning invoices with fake money.
/// It charges 100 ppk input fees, so the fee arithmetic is exercised for real.
///
/// Network-dependent, so tagged and excluded from CI:
///   dotnet test --filter Category=Live
/// Override the mint with CASHU_TEST_MINT.
/// </summary>
[Trait("Category", "Live")]
public class LiveMintTests
{
    private readonly ITestOutputHelper _log;
    private static readonly string MintUrl =
        Environment.GetEnvironmentVariable("CASHU_TEST_MINT") ?? "https://testnut.cashu.space";

    public LiveMintTests(ITestOutputHelper log) => _log = log;

    private class MintQuoteRequest
    {
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("unit")] public string Unit { get; set; } = "sat";
    }

    private class MintQuote
    {
        [JsonPropertyName("quote")] public string Quote { get; set; } = "";
        [JsonPropertyName("request")] public string Request { get; set; } = "";
        [JsonPropertyName("state")] public string? State { get; set; }
    }

    private class MintRequest
    {
        [JsonPropertyName("quote")] public string Quote { get; set; } = "";
        [JsonPropertyName("outputs")] public List<CashuBlindedMessage> Outputs { get; set; } = [];
    }

    /// <summary>
    /// Plays the CUSTOMER: mints fresh ecash at the test mint. Only tests need
    /// this -- the plugin never mints, it only receives and melts.
    /// </summary>
    private static async Task<List<CashuProof>> MintAsync(HttpClient http, CashuMintClient mint, long amount, CancellationToken ct)
    {
        var keyset = CashuWalletOps.PickActiveKeyset(await mint.GetKeysetsAsync(ct), "sat")!;
        var keys = await mint.GetKeysAsync(keyset.Id, ct);

        var quoteResp = await http.PostAsJsonAsync($"{mint.MintUrl}/v1/mint/quote/bolt11", new MintQuoteRequest { Amount = amount }, ct);
        quoteResp.EnsureSuccessStatusCode();
        var quote = (await quoteResp.Content.ReadFromJsonAsync<MintQuote>(ct))!;

        var outputs = CashuWalletOps.PrepareOutputs(keyset.Id, CashuMintClient.SplitAmount(amount, keys.Keys));
        // The test mint pays its own invoices almost immediately, but not
        // always before the first request -- retry briefly.
        for (var attempt = 0; ; attempt++)
        {
            var resp = await http.PostAsJsonAsync($"{mint.MintUrl}/v1/mint/bolt11",
                new MintRequest { Quote = quote.Quote, Outputs = outputs.Select(o => o.ToBlindedMessage()).ToList() }, ct);
            if (resp.IsSuccessStatusCode)
            {
                var sigs = (await resp.Content.ReadFromJsonAsync<CashuSwapResponse>(ct))!.Signatures;
                return outputs.Zip(sigs, (o, s) => CashuWalletOps.Unblind(o, s, keys)).ToList();
            }
            if (attempt >= 10) throw new Exception("Mint never paid its quote: " + await resp.Content.ReadAsStringAsync(ct));
            await Task.Delay(1000, ct);
        }
    }

    /// <summary>A real-looking BOLT11 the test mint can settle: one of its own.</summary>
    private static async Task<string> FakeInvoiceAsync(HttpClient http, string mintUrl, long amount, CancellationToken ct)
    {
        var resp = await http.PostAsJsonAsync($"{mintUrl}/v1/mint/quote/bolt11", new MintQuoteRequest { Amount = amount }, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MintQuote>(ct))!.Request;
    }

    [Fact]
    public async Task Receive_swap_then_melt_with_change_round_trip()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;
        using var http = new HttpClient();
        var mint = new CashuMintClient(http, MintUrl);

        // 1. Customer's ecash.
        var customerProofs = await MintAsync(http, mint, 100, ct);
        Assert.Equal(100, customerProofs.Sum(p => p.Amount));

        // 2. Receive it exactly the way the plugin does: swap into outputs
        //    only we know the secrets of, paying the input fee from the amount.
        var keysets = await mint.GetKeysetsAsync(ct);
        var fees = keysets.ToDictionary(k => k.Id, k => k.InputFeePpk ?? 0);
        var keyset = CashuWalletOps.PickActiveKeyset(keysets, "sat")!;
        var keys = await mint.GetKeysAsync(keyset.Id, ct);

        var inputFee = CashuMintClient.InputFee(customerProofs.Select(p => p.Id), fees);
        var net = 100 - inputFee;
        _log.WriteLine($"Received 100 sat in {customerProofs.Count} proofs, input fee {inputFee}, net {net}");

        var outputs = CashuWalletOps.PrepareOutputs(keyset.Id, CashuMintClient.SplitAmount(net, keys.Keys));
        var sigs = await mint.SwapAsync(customerProofs, outputs.Select(o => o.ToBlindedMessage()).ToList(), ct);
        var ours = outputs.Zip(sigs, (o, s) => CashuWalletOps.Unblind(o, s, keys)).ToList();
        Assert.Equal(net, ours.Sum(p => p.Amount));

        // 3. The customer's proofs are now spent: a second payment with the
        //    same ecash must be refused with the "already spent" code.
        var replayOutputs = CashuWalletOps.PrepareOutputs(keyset.Id, CashuMintClient.SplitAmount(net, keys.Keys));
        var ex = await Assert.ThrowsAsync<CashuMintException>(() =>
            mint.SwapAsync(customerProofs, replayOutputs.Select(o => o.ToBlindedMessage()).ToList(), ct));
        _log.WriteLine($"Replay rejected: code={ex.Code} {ex.Message}");
        Assert.True(ex.IsAlreadySpent);

        // 4. NUT-09 restore gives back the signatures for outputs it signed --
        //    the crash-recovery path.
        var restored = await mint.RestoreAsync(outputs.Select(o => o.ToBlindedMessage()).ToList(), ct);
        Assert.Equal(outputs.Count, restored.Signatures.Count);
        var restoredProofs = restored.Outputs
            .Zip(restored.Signatures, (bm, s) => CashuWalletOps.Unblind(outputs.Single(o => o.B_ == bm.B_), s, keys))
            .ToList();
        Assert.Equal(ours.Select(p => p.C).OrderBy(c => c), restoredProofs.Select(p => p.C).OrderBy(c => c));

        // 5. Settle to Lightning: melt for an invoice smaller than the
        //    balance, so the overshoot has to come back as NUT-08 change.
        var invoiceAmount = net - 20;
        var quote = await mint.CreateMeltQuoteAsync(await FakeInvoiceAsync(http, MintUrl, invoiceAmount, ct), ct);
        _log.WriteLine($"Melt quote {quote.Quote}: amount {quote.Amount}, fee_reserve {quote.FeeReserve}");
        Assert.Equal(invoiceAmount, quote.Amount);

        var selected = CashuWalletOps.SelectProofs(ours, p => p.Amount, p => p.Id, quote.Amount + quote.FeeReserve, fees)!;
        Assert.NotNull(selected);
        var meltFee = CashuMintClient.InputFee(selected.Select(p => p.Id), fees);
        var maxChange = selected.Sum(p => p.Amount) - meltFee - quote.Amount;
        var blanks = CashuWalletOps.PrepareBlankOutputs(keyset.Id, CashuMintClient.BlankOutputCount(maxChange));

        var result = await mint.MeltAsync(quote.Quote, selected, blanks.Select(b => b.ToBlindedMessage()).ToList(), ct);
        _log.WriteLine($"Melt state {result.State}, change {result.Change?.Count ?? 0} signatures");
        Assert.Equal("PAID", result.State);

        var change = (result.Change ?? [])
            .Select((s, i) => CashuWalletOps.Unblind(blanks[i], s, keys))
            .ToList();
        var changeSum = change.Sum(p => p.Amount);
        _log.WriteLine($"Change returned: {changeSum} sat in {change.Count} proofs (max possible {maxChange})");
        Assert.True(changeSum > 0 && changeSum <= maxChange);

        // 6. Change is real money: spend it once more, and confirm the melted
        //    inputs are SPENT per NUT-07.
        var states = await mint.CheckStateAsync(selected.Select(p => CashuWalletOps.Y(p.Secret)).ToList(), ct);
        Assert.All(states, s => Assert.Equal("SPENT", s.State));

        var changeFee = CashuMintClient.InputFee(change.Select(p => p.Id), fees);
        var reswap = CashuWalletOps.PrepareOutputs(keyset.Id, CashuMintClient.SplitAmount(changeSum - changeFee, keys.Keys));
        var reswapSigs = await mint.SwapAsync(change, reswap.Select(o => o.ToBlindedMessage()).ToList(), ct);
        Assert.Equal(changeSum - changeFee, reswapSigs.Sum(s => s.Amount));
    }

    [Fact]
    public async Task Exported_token_round_trips_through_a_v4_encoding()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        using var http = new HttpClient();
        var mint = new CashuMintClient(http, MintUrl);
        var proofs = await MintAsync(http, mint, 8, cts.Token);

        var token = CashuEncoding.EncodeTokenV4(mint.MintUrl, "sat", proofs);
        _log.WriteLine(token);
        Assert.StartsWith("cashuB", token);
        Assert.DoesNotContain("=", token);
    }
}
