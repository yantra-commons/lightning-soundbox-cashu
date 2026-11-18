using System;
using System.Collections.Generic;
using BTCPayServer.Plugins.SoundboxCashu.Cashu;
using Xunit;

namespace BTCPayServer.Plugins.SoundboxCashu.Tests;

/// <summary>
/// Offline checks against the official Cashu test vectors
/// (github.com/cashubtc/nuts, tests/00-tests.md and tests/18-tests.md).
/// If any of these fail, the plugin would produce outputs a real mint cannot
/// sign or requests a real wallet cannot read -- so they are exact-match.
/// </summary>
public class CashuProtocolTests
{
    // ── NUT-00 hash_to_curve ──

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000",
                "024cce997d3b518f739663b757deaec95bcd9473c30a14ac2fd04023a739d1a725")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000001",
                "022e7158e11c9506f1aa4248bf531298daa7febd6194f003edcd9b93ade6253acf")]
    // Needs several counter iterations before it lands on the curve.
    [InlineData("0000000000000000000000000000000000000000000000000000000000000002",
                "026cdbe15362df59cd1dd3c9c11de8aedac2106eca69236ecd9fbe117af897be4f")]
    public void HashToCurve_matches_spec_vectors(string messageHex, string expectedPoint)
    {
        var y = CashuCrypto.HashToCurve(Convert.FromHexString(messageHex));
        Assert.Equal(expectedPoint, CashuCrypto.ToHex(y));
    }

    // ── NUT-00 blinding ──

    [Theory]
    [InlineData("d341ee4871f1f889041e63cf0d3823c713eea6aff01e80f1719f08f9e5be98f6",
                "99fce58439fc37412ab3468b73db0569322588f62fb3a49182d67e23d877824a",
                "033b1a9737a40cc3fd9b6af4b723632b76a67a36782596304612a6c2bfb5197e6d")]
    [InlineData("f1aaf16c2239746f369572c0784d9dd3d032d952c2d992175873fb58fae31a60",
                "f78476ea7cc9ade20f9e05e58a804cf19533f03ea805ece5fee88c8e2874ba50",
                "029bdf2d716ee366eddf599ba252786c1033f47e230248a4612a5670ab931f1763")]
    public void Blind_matches_spec_vectors(string xHex, string rHex, string expectedB)
    {
        var y = CashuCrypto.HashToCurve(Convert.FromHexString(xHex));
        var b = CashuCrypto.Blind(y, CashuCrypto.PrivKeyFromHex(rHex));
        Assert.Equal(expectedB, CashuCrypto.ToHex(b));
    }

    // ── NUT-00 mint signing (C_ = kB_) -- checks our scalar multiplication ──

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000001",
                "02a9acc1e48c25eeeb9289b5031cc57da9fe72f3fe2861d264bdc074209b107ba2",
                "02a9acc1e48c25eeeb9289b5031cc57da9fe72f3fe2861d264bdc074209b107ba2")]
    [InlineData("7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f",
                "02a9acc1e48c25eeeb9289b5031cc57da9fe72f3fe2861d264bdc074209b107ba2",
                "0398bc70ce8184d27ba89834d19f5199c84443c31131e48d3c1214db24247d005d")]
    public void Scalar_multiplication_matches_spec_signing_vectors(string kHex, string bHex, string expectedC)
    {
        var c = CashuCrypto.PubKeyFromHex(bHex).GetSharedPubkey(CashuCrypto.PrivKeyFromHex(kHex));
        Assert.Equal(expectedC, CashuCrypto.ToHex(c));
    }

    /// <summary>
    /// The spec has no unblinding vector, so check the identity it rests on:
    /// for a mint key k, unblinding k(Y + rG) with K = kG must give exactly kY
    /// -- the value the mint will later verify the proof against.
    /// </summary>
    [Fact]
    public void Unblind_recovers_the_mints_signature_on_Y()
    {
        var k = CashuCrypto.PrivKeyFromHex("7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f");
        var mintPub = k.CreatePubKey();

        for (var i = 0; i < 20; i++)
        {
            var secret = CashuCrypto.NewSecret();
            var y = CashuCrypto.HashToCurve(secret);
            var r = CashuCrypto.NewBlindingFactor();

            var blinded = CashuCrypto.Blind(y, r);
            var blindSig = blinded.GetSharedPubkey(k);          // what the mint returns
            var c = CashuCrypto.Unblind(blindSig, r, mintPub);  // what we store

            Assert.Equal(CashuCrypto.ToHex(y.GetSharedPubkey(k)), CashuCrypto.ToHex(c));
        }
    }

    [Fact]
    public void Secrets_are_64_lowercase_hex_and_unique()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 100; i++)
        {
            var s = CashuCrypto.NewSecret();
            Assert.Matches("^[0-9a-f]{64}$", s);
            Assert.True(seen.Add(s));
        }
    }

    // ── NUT-18 payment request encoding ──

    [Fact]
    public void PaymentRequest_matches_the_http_transport_vector()
    {
        var encoded = CashuEncoding.EncodePaymentRequest(new CashuPaymentRequest
        {
            Id = "a2c12f45",
            Amount = 50,
            Unit = "sat",
            Mints = ["https://cashu.example.com"],
            Transports = [new CashuTransport("post", "https://api.example.com/receive")]
        });

        // The spec vector, verbatim. Its reference encoder writes the optional
        // transport tags as an explicit `"g": null`; NUT-18 marks `g` optional
        // and we omit it. So the comparison removes exactly that one entry
        // (key "g" = 61 67, value null = f7, and the transport map shrinks
        // from 3 entries to 2) and then demands every remaining byte match.
        const string vector =
            "creqApWF0gaNhdGRwb3N0YWF4H2h0dHBzOi8vYXBpLmV4YW1wbGUuY29tL3JlY2VpdmVhZ/dhaWhhMmMxMmY0NWFhGDJhdWNzYXRhbYF4GWh0dHBzOi8vY2FzaHUuZXhhbXBsZS5jb20=";
        var expectedHex = Convert.ToHexString(Convert.FromBase64String(vector["creqA".Length..])).ToLowerInvariant();
        Assert.Contains("a3617464706f7374", expectedHex);
        Assert.Contains("6167f7", expectedHex);
        expectedHex = expectedHex
            .Replace("a3617464706f7374", "a2617464706f7374")
            .Replace("6167f7", "");

        var actualHex = Convert.ToHexString(DecodeBase64Url(encoded["creqA".Length..])).ToLowerInvariant();
        Assert.Equal(expectedHex, actualHex);
    }

    private static byte[] DecodeBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    [Fact]
    public void PaymentRequest_matches_the_minimal_vector()
    {
        var encoded = CashuEncoding.EncodePaymentRequest(new CashuPaymentRequest
        {
            Id = "7f4a2b39",
            Unit = "sat",
            Mints = ["https://mint.example.com"]
        });

        Assert.Equal("creqAo2FpaDdmNGEyYjM5YXVjc2F0YW2BeBhodHRwczovL21pbnQuZXhhbXBsZS5jb20=", encoded);
    }

    // ── NUT-00 V4 token encoding ──

    [Fact]
    public void TokenV4_matches_the_single_keyset_vector()
    {
        var token = CashuEncoding.EncodeTokenV4("http://localhost:3338", "sat",
        [
            new CashuProof
            {
                Amount = 1, Id = "00ad268c4d1f5826",
                Secret = "9a6dbb847bd232ba76db0df197216b29d3b8cc14553cd27827fc1cc942fedb4e",
                C = "038618543ffb6b8695df4ad4babcde92a34a96bdcd97dcee0d7ccf98d472126792"
            }
        ], memo: "Thank you");

        // The vector is written padded; we emit unpadded, which decoders accept.
        Assert.Equal(
            "cashuBpGF0gaJhaUgArSaMTR9YJmFwgaNhYQFhc3hAOWE2ZGJiODQ3YmQyMzJiYTc2ZGIwZGYxOTcyMTZiMjlkM2I4Y2MxNDU1M2NkMjc4MjdmYzFjYzk0MmZlZGI0ZWFjWCEDhhhUP_trhpXfStS6vN6So0qWvc2X3O4NfM-Y1HISZ5JhZGlUaGFuayB5b3VhbXVodHRwOi8vbG9jYWxob3N0OjMzMzhhdWNzYXQ",
            token);
    }

    [Fact]
    public void TokenV4_matches_the_multi_keyset_vector()
    {
        var token = CashuEncoding.EncodeTokenV4("http://localhost:3338", "sat",
        [
            new CashuProof
            {
                Amount = 1, Id = "00ffd48b8f5ecf80",
                Secret = "acc12435e7b8484c3cf1850149218af90f716a52bf4a5ed347e48ecc13f77388",
                C = "0244538319de485d55bed3b29a642bee5879375ab9e7a620e11e48ba482421f3cf"
            },
            new CashuProof
            {
                Amount = 2, Id = "00ad268c4d1f5826",
                Secret = "1323d3d4707a58ad2e23ada4e9f1f49f5a5b4ac7b708eb0d61f738f48307e8ee",
                C = "023456aa110d84b4ac747aebd82c3b005aca50bf457ebd5737a4414fac3ae7d94d"
            },
            new CashuProof
            {
                Amount = 1, Id = "00ad268c4d1f5826",
                Secret = "56bcbcbb7cc6406b3fa5d57d2174f4eff8b4402b176926d3a57d3c3dcbb59d57",
                C = "0273129c5719e599379a974a626363c333c56cafc0e6d01abe46d5808280789c63"
            }
        ]);

        Assert.Equal(
            "cashuBo2F0gqJhaUgA_9SLj17PgGFwgaNhYQFhc3hAYWNjMTI0MzVlN2I4NDg0YzNjZjE4NTAxNDkyMThhZjkwZjcxNmE1MmJmNGE1ZWQzNDdlNDhlY2MxM2Y3NzM4OGFjWCECRFODGd5IXVW-07KaZCvuWHk3WrnnpiDhHki6SCQh88-iYWlIAK0mjE0fWCZhcIKjYWECYXN4QDEzMjNkM2Q0NzA3YTU4YWQyZTIzYWRhNGU5ZjFmNDlmNWE1YjRhYzdiNzA4ZWIwZDYxZjczOGY0ODMwN2U4ZWVhY1ghAjRWqhENhLSsdHrr2Cw7AFrKUL9Ffr1XN6RBT6w659lNo2FhAWFzeEA1NmJjYmNiYjdjYzY0MDZiM2ZhNWQ1N2QyMTc0ZjRlZmY4YjQ0MDJiMTc2OTI2ZDNhNTdkM2MzZGNiYjU5ZDU3YWNYIQJzEpxXGeWZN5qXSmJjY8MzxWyvwObQGr5G1YCCgHicY2FtdWh0dHA6Ly9sb2NhbGhvc3Q6MzMzOGF1Y3NhdA",
            token);
    }

    // ── Amount and fee arithmetic ──

    private static readonly long[] PowersOfTwo = BuildPowers();

    private static long[] BuildPowers()
    {
        var p = new long[21];
        for (var i = 0; i < p.Length; i++) p[i] = 1L << i;
        return p;
    }

    [Theory]
    [InlineData(1, new long[] { 1 })]
    [InlineData(13, new long[] { 1, 4, 8 })]
    [InlineData(64, new long[] { 64 })]
    [InlineData(1000, new long[] { 8, 32, 64, 128, 256, 512 })]
    public void SplitAmount_is_the_ascending_binary_expansion(long amount, long[] expected)
    {
        Assert.Equal(expected, CashuMintClient.SplitAmount(amount, PowersOfTwo));
    }

    [Fact]
    public void SplitAmount_refuses_amounts_the_keyset_cannot_represent()
    {
        Assert.Throws<InvalidOperationException>(() => CashuMintClient.SplitAmount(3, new long[] { 2, 4 }));
    }

    [Fact]
    public void InputFee_rounds_up_once_over_the_total()
    {
        var fees = new Dictionary<string, long> { ["a"] = 100, ["b"] = 0 };
        // The spec's own example: three inputs at 100 ppk cost 1 sat.
        Assert.Equal(1, CashuMintClient.InputFee(["a", "a", "a"], fees));
        Assert.Equal(0, CashuMintClient.InputFee(["b", "b"], fees));
        Assert.Equal(2, CashuMintClient.InputFee(Repeat("a", 11), fees));
        // An unknown keyset contributes nothing rather than throwing.
        Assert.Equal(1, CashuMintClient.InputFee(["a", "zzz"], fees));
    }

    private static List<string> Repeat(string s, int n)
    {
        var l = new List<string>();
        for (var i = 0; i < n; i++) l.Add(s);
        return l;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(1000, 10)] // NUT-08's worked example
    public void BlankOutputCount_follows_nut08(long feeReserve, int expected)
    {
        Assert.Equal(expected, CashuMintClient.BlankOutputCount(feeReserve));
    }

    [Theory]
    [InlineData("https://Mint.Example.com/", "https://mint.example.com")]
    [InlineData("https://mint.minibits.cash/Bitcoin", "https://mint.minibits.cash/Bitcoin")]
    [InlineData("  https://testnut.cashu.space  ", "https://testnut.cashu.space")]
    public void Mint_urls_compare_case_insensitively_on_host_only(string input, string expected)
    {
        Assert.Equal(expected, CashuMintClient.NormalizeMintUrl(input));
    }
}
