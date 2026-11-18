using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SoundboxCashu.Cashu;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.SoundboxCashu.Tests;

/// <summary>
/// Full stack, against the regtest environment in e2e/: a real BTCPay Server
/// running BOTH plugins (this one and the unmodified Lightning Soundbox
/// plugin), a real Nutshell mint backed by a real LND node, and a Lightning
/// channel between the mint's node and the store's node.
///
/// Plays two parts:
///   the CUSTOMER -- buys ecash at the mint, then pays the store with it the
///                   way a wallet answers a NUT-18 "post" request;
///   the SOUNDBOX -- long-polls the Lightning Soundbox device API with the
///                   device's own HMAC signature, exactly as the ESP32 does,
///                   and waits for the payment_settled event it would speak.
///
///   dotnet test --filter Category=E2E      (see e2e/README.md for setup)
/// </summary>
[Trait("Category", "E2E")]
public class EndToEndTests
{
    private readonly ITestOutputHelper _log;
    public EndToEndTests(ITestOutputHelper log) => _log = log;

    private static string Env(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} not set");

    [Fact]
    public async Task Ecash_payment_is_announced_by_the_soundbox()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var btcpay = Env("E2E_BTCPAY");
        var store = Env("E2E_STORE");
        var mint = new CashuMintClient(http, Env("E2E_MINT"));
        var device = new Device(http, btcpay);

        var (proofs, keysets) = await BuyEcashAsync(http, mint, 1000, ct);
        _log.WriteLine($"Customer holds {proofs.Sum(p => p.Amount)} sat in {proofs.Count} proofs");

        // A browser wallet (cashu.me) sends a CORS preflight first.
        var preflight = new HttpRequestMessage(HttpMethod.Options, $"{btcpay}/plugins/sbcashu/{store}/pay");
        preflight.Headers.Add("Origin", "https://wallet.cashu.me");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
        var pre = await http.SendAsync(preflight, ct);
        _log.WriteLine($"Preflight: {(int)pre.StatusCode}");
        Assert.True(pre.Headers.Contains("Access-Control-Allow-Origin"));

        var cursor = await device.CurrentCursorAsync(ct);

        // Amounts as strings and a DLEQ blob, as cashu-ts may send them.
        var payload = Payload(proofs, mint.MintUrl, withDleq: true);
        var resp = await Post(http, btcpay, store, payload, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        _log.WriteLine($"Pay: {(int)resp.StatusCode} {body}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var net = JsonNode.Parse(body)!["amount"]!.GetValue<long>();
        Assert.Equal(1000 - CashuMintClient.InputFee(proofs.Select(p => p.Id),
            keysets.ToDictionary(k => k.Id, k => k.InputFeePpk ?? 0)), net);

        // Paying twice with the same ecash is refused.
        var replay = await Post(http, btcpay, store, payload, ct);
        var replayBody = await replay.Content.ReadAsStringAsync(ct);
        _log.WriteLine($"Replay: {(int)replay.StatusCode} {replayBody}");
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains("already been spent", replayBody);

        // The soundbox hears about it.
        var settled = await device.WaitForPaymentAsync(cursor, TimeSpan.FromMinutes(2), ct);
        Assert.NotNull(settled);
        var spoken = settled!["amount_sat"]!.GetValue<long>();
        _log.WriteLine($"Soundbox received payment_settled: {spoken} sat (invoice {settled["invoice_id"]})");
        Assert.InRange(spoken, 1, net);
    }

    /// <summary>
    /// The shop's Lightning node is down when the customer pays. The customer
    /// must still be accepted -- the swap makes the ecash the store's either
    /// way -- and the box must announce it once the node is back, unaided.
    /// </summary>
    [Fact]
    public async Task Payment_taken_while_node_is_down_is_announced_when_it_returns()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = cts.Token;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var btcpay = Env("E2E_BTCPAY");
        var store = Env("E2E_STORE");
        var mint = new CashuMintClient(http, Env("E2E_MINT"));
        var device = new Device(http, btcpay);

        var (proofs, _) = await BuyEcashAsync(http, mint, 500, ct);
        var cursor = await device.CurrentCursorAsync(ct);

        Compose("stop", "merchant_lnd");
        DateTime restartedAt;
        try
        {
            var resp = await Post(http, btcpay, store, Payload(proofs, mint.MintUrl, withDleq: false), ct);
            _log.WriteLine($"Pay with node down: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(ct)}");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            // Nothing can be announced while the node is down.
            Assert.Null(await device.WaitForPaymentAsync(cursor, TimeSpan.FromSeconds(10), ct));
        }
        finally
        {
            Compose("start", "merchant_lnd");
            restartedAt = DateTime.UtcNow;
        }

        // Settlement retries with backoff, so allow a few minutes.
        var settled = await device.WaitForPaymentAsync(cursor, TimeSpan.FromMinutes(6), ct);
        Assert.NotNull(settled);
        _log.WriteLine($"Announced after node returned: {settled!["amount_sat"]} sat, " +
                       $"{(DateTime.UtcNow - restartedAt).TotalSeconds:F0}s after restart");
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static Task<HttpResponseMessage> Post(HttpClient http, string btcpay, string store, string payload, CancellationToken ct) =>
        http.PostAsync($"{btcpay}/plugins/sbcashu/{store}/pay",
            new StringContent(payload, Encoding.UTF8, "application/json"), ct);

    /// <summary>The customer buys ecash, paying the mint's invoice from E2E_PAYER_LND.</summary>
    private static async Task<(List<CashuProof> Proofs, List<CashuKeysetInfo> Keysets)> BuyEcashAsync(
        HttpClient http, CashuMintClient mint, long amount, CancellationToken ct)
    {
        var keysets = await mint.GetKeysetsAsync(ct);
        var keyset = CashuWalletOps.PickActiveKeyset(keysets, "sat")!;
        var keys = await mint.GetKeysAsync(keyset.Id, ct);

        var quote = await (await http.PostAsJsonAsync($"{mint.MintUrl}/v1/mint/quote/bolt11",
            new { amount, unit = "sat" }, ct)).Content.ReadFromJsonAsync<JsonObject>(ct);
        var pay = await http.PostAsJsonAsync($"{Env("E2E_PAYER_LND")}/v1/channels/transactions",
            new { payment_request = quote!["request"]!.GetValue<string>() }, ct);
        var payBody = await pay.Content.ReadAsStringAsync(ct);
        Assert.True(pay.IsSuccessStatusCode &&
                    JsonNode.Parse(payBody)?["payment_error"]?.GetValue<string>() is null or "", payBody);

        var outputs = CashuWalletOps.PrepareOutputs(keyset.Id, CashuMintClient.SplitAmount(amount, keys.Keys));
        var mintResp = await http.PostAsJsonAsync($"{mint.MintUrl}/v1/mint/bolt11", new
        {
            quote = quote["quote"]!.GetValue<string>(),
            outputs = outputs.Select(o => o.ToBlindedMessage())
        }, ct);
        mintResp.EnsureSuccessStatusCode();
        var sigs = (await mintResp.Content.ReadFromJsonAsync<CashuSwapResponse>(ct))!.Signatures;
        return (outputs.Zip(sigs, (o, s) => CashuWalletOps.Unblind(o, s, keys)).ToList(), keysets);
    }

    /// <summary>
    /// The NUT-18 payment payload. "mint" is the mint as the STORE knows it:
    /// in the docker stack that is an in-network hostname, which differs from
    /// the URL this test reaches it on.
    /// </summary>
    private static string Payload(IEnumerable<CashuProof> proofs, string mintUrl, bool withDleq) => new JsonObject
    {
        ["mint"] = Environment.GetEnvironmentVariable("E2E_MINT_AS_CONFIGURED") ?? mintUrl,
        ["unit"] = "sat",
        ["proofs"] = new JsonArray(proofs.Select(p =>
        {
            var o = new JsonObject { ["id"] = p.Id, ["amount"] = p.Amount.ToString(), ["secret"] = p.Secret, ["C"] = p.C };
            if (withDleq) o["dleq"] = new JsonObject { ["e"] = "00", ["s"] = "00", ["r"] = "00" };
            return (JsonNode)o;
        }).ToArray())
    }.ToJsonString();

    private static void Compose(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("docker", "compose " + string.Join(' ', args))
        {
            WorkingDirectory = Env("E2E_COMPOSE_DIR"),
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    /// <summary>
    /// A soundbox, as far as the server can tell: the same signed long-poll
    /// the firmware makes (event_stream.cpp / net_client.cpp).
    /// </summary>
    private sealed class Device(HttpClient http, string btcpay)
    {
        private readonly string _id = Env("E2E_DEVICE_ID");
        private readonly string _secret = Env("E2E_DEVICE_SECRET");

        private async Task<JsonArray?> PollAsync(long since, CancellationToken ct)
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var canonical = $"GET\n{_id}\n{ts}\n{since}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
            var sig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

            var req = new HttpRequestMessage(HttpMethod.Get,
                $"{btcpay}/plugins/lightningsoundbox/api/v1/devices/{_id}/events?since={since}&firmware=e2e");
            req.Headers.TryAddWithoutValidation("Authorization", $"LSB1-HMAC-SHA256 {sig}");
            req.Headers.Add("X-LSB-Timestamp", ts.ToString());
            var resp = await http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NoContent) return null;
            resp.EnsureSuccessStatusCode();
            return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!["events"]!.AsArray();
        }

        /// <summary>Acknowledges everything already queued, returning the cursor.</summary>
        public async Task<long> CurrentCursorAsync(CancellationToken ct)
        {
            long cursor = 0;
            while (await PollAsync(cursor, ct) is { Count: > 0 } events)
                cursor = events.Max(e => e!["id"]!.GetValue<long>());
            return cursor;
        }

        /// <summary>Long-polls until a payment_settled event arrives, or the timeout.</summary>
        public async Task<JsonNode?> WaitForPaymentAsync(long since, TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(timeout);
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    var events = await PollAsync(since, window.Token);
                    if (events is null) continue;
                    var paid = events.FirstOrDefault(e => e!["type"]!.GetValue<string>() == "payment_settled");
                    if (paid is not null) return paid["data"];
                    since = events.Max(e => e!["id"]!.GetValue<long>());
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            return null;
        }
    }
}
