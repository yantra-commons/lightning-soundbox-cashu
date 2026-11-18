using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.SoundboxCashu.Cashu;

/// <summary>A mint answered, and said no. The operation definitely did not happen.</summary>
public class CashuMintException : Exception
{
    public int? Code { get; }
    public int HttpStatus { get; }

    public CashuMintException(int httpStatus, int? code, string? detail)
        : base(detail ?? $"Mint returned HTTP {httpStatus}")
    {
        HttpStatus = httpStatus;
        Code = code;
    }

    /// <summary>NUT error 11001: the proofs were already spent.</summary>
    public bool IsAlreadySpent => Code == 11001;
}

/// <summary>
/// Thin client for one mint's HTTP API.
///
/// The important distinction it preserves is between a mint that REFUSED a
/// request (<see cref="CashuMintException"/>: nothing happened, safe to undo
/// local bookkeeping) and a request whose outcome is UNKNOWN (any other
/// exception -- a timeout or dropped connection, after which the mint may or
/// may not have acted). Callers must never treat the second like the first,
/// or a swap that did succeed would have its outputs thrown away.
/// </summary>
public class CashuMintClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // Wallets built on cashu-ts serialise large amounts as strings to
        // stay exact in JavaScript, so accept either form on the way in.
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Keyset keys are immutable for a given id (the id is derived from them),
    // so they can be cached for the life of the process.
    private static readonly ConcurrentDictionary<string, Dictionary<long, string>> KeyCache = new();

    private readonly HttpClient _http;

    public string MintUrl { get; }

    public CashuMintClient(HttpClient http, string mintUrl)
    {
        _http = http;
        MintUrl = NormalizeMintUrl(mintUrl);
    }

    /// <summary>
    /// Canonical form used for every comparison: no trailing slash, lowercase
    /// scheme and host. The path keeps its case, because some mints are
    /// served under a case-sensitive path such as "/Bitcoin".
    /// </summary>
    public static string NormalizeMintUrl(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return trimmed;
        var authority = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        var path = uri.AbsolutePath.TrimEnd('/');
        return authority + path;
    }

    public Task<CashuMintInfo> GetInfoAsync(CancellationToken ct) =>
        GetAsync<CashuMintInfo>("/v1/info", ct);

    public async Task<List<CashuKeysetInfo>> GetKeysetsAsync(CancellationToken ct) =>
        (await GetAsync<CashuKeysetsResponse>("/v1/keysets", ct)).Keysets;

    /// <summary>Public keys of one keyset, amount -> key hex.</summary>
    public async Task<Dictionary<long, string>> GetKeysAsync(string keysetId, CancellationToken ct)
    {
        var cacheKey = MintUrl + "|" + keysetId;
        if (KeyCache.TryGetValue(cacheKey, out var cached)) return cached;

        var response = await GetAsync<CashuKeysResponse>($"/v1/keys/{Uri.EscapeDataString(keysetId)}", ct);
        var keyset = response.Keysets.FirstOrDefault(k => k.Id == keysetId)
                     ?? throw new InvalidOperationException($"Mint did not return keyset {keysetId}");

        var keys = new Dictionary<long, string>();
        foreach (var (amount, key) in keyset.Keys)
        {
            if (long.TryParse(amount, out var a) && a > 0) keys[a] = key;
        }

        KeyCache[cacheKey] = keys;
        return keys;
    }

    public async Task<List<CashuBlindSignature>> SwapAsync(
        List<CashuProof> inputs, List<CashuBlindedMessage> outputs, CancellationToken ct) =>
        (await PostAsync<CashuSwapResponse>("/v1/swap", new CashuSwapRequest { Inputs = inputs, Outputs = outputs }, ct))
        .Signatures;

    public Task<CashuMeltQuote> CreateMeltQuoteAsync(string bolt11, CancellationToken ct) =>
        PostAsync<CashuMeltQuote>("/v1/melt/quote/bolt11", new CashuMeltQuoteRequest { Request = bolt11 }, ct);

    public Task<CashuMeltQuote> GetMeltQuoteAsync(string quoteId, CancellationToken ct) =>
        GetAsync<CashuMeltQuote>($"/v1/melt/quote/bolt11/{Uri.EscapeDataString(quoteId)}", ct);

    /// <summary>
    /// Pays the quoted Lightning invoice. This call blocks for as long as the
    /// mint's Lightning payment takes, so it is given its own long timeout.
    /// </summary>
    public Task<CashuMeltQuote> MeltAsync(
        string quoteId, List<CashuProof> inputs, List<CashuBlindedMessage> blankOutputs, CancellationToken ct) =>
        PostAsync<CashuMeltQuote>("/v1/melt/bolt11",
            new CashuMeltRequest { Quote = quoteId, Inputs = inputs, Outputs = blankOutputs }, ct,
            TimeSpan.FromMinutes(3));

    /// <summary>
    /// NUT-09: asks the mint for signatures it previously issued on these
    /// blinded messages. This is how an interrupted swap or melt is recovered:
    /// the outputs were stored before the request was sent, so if the mint
    /// signed them we can still unblind the result.
    /// </summary>
    public Task<CashuRestoreResponse> RestoreAsync(List<CashuBlindedMessage> outputs, CancellationToken ct) =>
        PostAsync<CashuRestoreResponse>("/v1/restore", new CashuRestoreRequest { Outputs = outputs }, ct);

    /// <summary>NUT-07: UNSPENT / PENDING / SPENT per Y, in request order.</summary>
    public async Task<List<CashuProofStateEntry>> CheckStateAsync(List<string> ys, CancellationToken ct) =>
        (await PostAsync<CashuCheckStateResponse>("/v1/checkstate", new CashuCheckStateRequest { Ys = ys }, ct)).States;

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _http.GetAsync(MintUrl + path, cts.Token);
        return await ReadAsync<T>(response, cts.Token);
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        using var content = new StringContent(
            JsonSerializer.Serialize(body, body.GetType(), JsonOpts), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(MintUrl + path, content, cts.Token);
        return await ReadAsync<T>(response, cts.Token);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // A 4xx is the mint deciding; anything else (5xx, a proxy error
            // page) says nothing reliable about whether it acted.
            if ((int)response.StatusCode is >= 400 and < 500)
            {
                CashuMintError? err = null;
                try { err = JsonSerializer.Deserialize<CashuMintError>(text, JsonOpts); }
                catch (JsonException) { }
                throw new CashuMintException((int)response.StatusCode, err?.Code, err?.Detail ?? Truncate(text));
            }
            throw new HttpRequestException($"Mint returned HTTP {(int)response.StatusCode}: {Truncate(text)}");
        }

        return JsonSerializer.Deserialize<T>(text, JsonOpts)
               ?? throw new HttpRequestException("Mint returned an empty response");
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";

    /// <summary>
    /// NUT-02 input fee: the per-input fees of each proof's keyset, summed in
    /// parts-per-thousand and rounded up once. Integer arithmetic on purpose:
    /// the spec warns that a floating-point ceil can disagree with the mint.
    /// </summary>
    public static long InputFee(IEnumerable<string> inputKeysetIds, IReadOnlyDictionary<string, long> feePpkByKeyset)
    {
        long sum = 0;
        foreach (var id in inputKeysetIds)
            sum += feePpkByKeyset.TryGetValue(id, out var ppk) ? ppk : 0;
        return (sum + 999) / 1000;
    }

    /// <summary>
    /// Splits an amount into the keyset's denominations, largest first.
    /// Standard keysets are powers of two, so this is the binary expansion;
    /// it is still checked against the keys actually offered rather than
    /// assumed, because a mint that lacks a denomination would otherwise get
    /// an output it cannot sign.
    /// </summary>
    public static List<long> SplitAmount(long amount, IReadOnlyCollection<long> denominations)
    {
        if (amount <= 0) return [];
        var result = new List<long>();
        var remaining = amount;
        foreach (var d in denominations.Where(d => d > 0).OrderByDescending(d => d))
        {
            while (remaining >= d)
            {
                result.Add(d);
                remaining -= d;
            }
        }
        if (remaining != 0)
            throw new InvalidOperationException($"Keyset cannot represent {amount} exactly");
        // NUT-03 asks for ascending order so output order leaks nothing.
        result.Sort();
        return result;
    }

    /// <summary>NUT-08: blank outputs needed to receive up to <paramref name="maxChange"/> back.</summary>
    public static int BlankOutputCount(long maxChange) =>
        maxChange <= 0 ? 0 : Math.Max((int)Math.Ceiling(Math.Log2(maxChange)), 1);
}
