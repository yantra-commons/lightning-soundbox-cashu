using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BTCPayServer.Plugins.SoundboxCashu.Cashu;

/// <summary>
/// The two Cashu string formats this plugin produces:
///
///   creqA... -- a NUT-18 payment request, shown as a QR on the soundbox
///   cashuB... -- a NUT-00 V4 token, used when a merchant exports their
///                balance to their own wallet
///
/// Both are CBOR, base64url-encoded behind a short prefix. Only the handful
/// of CBOR types they use are implemented (unsigned ints, byte and text
/// strings, arrays, maps with text keys, booleans), which is small enough to
/// check byte-for-byte against the spec's test vectors rather than pull in a
/// general-purpose CBOR library.
/// </summary>
public static class CashuEncoding
{
    /// <summary>
    /// Encodes a NUT-18 payment request.
    ///
    /// Field order follows the spec's own HTTP-transport test vector (t, i, a,
    /// u, m), so for the same inputs the output is byte-identical to it.
    /// Absent fields are omitted, not written as null.
    /// </summary>
    public static string EncodePaymentRequest(CashuPaymentRequest request)
    {
        var map = new List<(string, object)>();
        if (request.Transports.Count > 0)
        {
            map.Add(("t", request.Transports
                .Select(t => (object)new List<(string, object)> { ("t", t.Type), ("a", t.Target) })
                .ToList()));
        }
        if (request.Id is not null) map.Add(("i", request.Id));
        if (request.Amount is not null) map.Add(("a", (ulong)request.Amount.Value));
        if (request.Unit is not null) map.Add(("u", request.Unit));
        if (request.SingleUse is not null) map.Add(("s", request.SingleUse.Value));
        if (request.Mints.Count > 0) map.Add(("m", request.Mints.Cast<object>().ToList()));
        if (request.Description is not null) map.Add(("d", request.Description));

        // cashu-ts, the library behind most web and mobile wallets, emits
        // padded base64url here; its decoder and every other one we know of
        // accept either form.
        return "creqA" + Base64Url(Cbor.Encode(map), pad: true);
    }

    /// <summary>
    /// Encodes proofs from one mint as a V4 token. Proofs are grouped by
    /// keyset, as the format requires: the keyset id is written once per
    /// group as bytes, and each proof's C as its 33 raw bytes.
    /// </summary>
    public static string EncodeTokenV4(string mintUrl, string unit, IEnumerable<CashuProof> proofs, string? memo = null)
    {
        var groups = proofs
            .GroupBy(p => p.Id)
            .Select(g => (object)new List<(string, object)>
            {
                ("i", Convert.FromHexString(g.Key)),
                ("p", g.Select(p => (object)new List<(string, object)>
                {
                    ("a", (ulong)p.Amount),
                    ("s", p.Secret),
                    ("c", Convert.FromHexString(p.C))
                }).ToList())
            })
            .ToList();

        var map = new List<(string, object)> { ("t", groups) };
        if (memo is not null) map.Add(("d", memo));
        map.Add(("m", mintUrl));
        map.Add(("u", unit));

        return "cashuB" + Base64Url(Cbor.Encode(map), pad: false);
    }

    internal static string Base64Url(byte[] data, bool pad)
    {
        var s = Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_');
        return pad ? s : s.TrimEnd('=');
    }

    /// <summary>Minimal deterministic CBOR (RFC 8949) encoder.</summary>
    internal static class Cbor
    {
        public static byte[] Encode(object value)
        {
            using var ms = new MemoryStream();
            Write(ms, value);
            return ms.ToArray();
        }

        private static void Write(Stream s, object value)
        {
            switch (value)
            {
                case ulong u:
                    WriteHead(s, 0, u);
                    break;
                case long l when l >= 0:
                    WriteHead(s, 0, (ulong)l);
                    break;
                case int i when i >= 0:
                    WriteHead(s, 0, (ulong)i);
                    break;
                case byte[] bytes:
                    WriteHead(s, 2, (ulong)bytes.Length);
                    s.Write(bytes);
                    break;
                case string text:
                    var utf8 = Encoding.UTF8.GetBytes(text);
                    WriteHead(s, 3, (ulong)utf8.Length);
                    s.Write(utf8);
                    break;
                case bool b:
                    s.WriteByte(b ? (byte)0xF5 : (byte)0xF4);
                    break;
                case List<(string, object)> map:
                    WriteHead(s, 5, (ulong)map.Count);
                    foreach (var (key, v) in map)
                    {
                        Write(s, key);
                        Write(s, v);
                    }
                    break;
                case List<object> array:
                    WriteHead(s, 4, (ulong)array.Count);
                    foreach (var item in array) Write(s, item);
                    break;
                default:
                    throw new ArgumentException($"Unsupported CBOR value type: {value?.GetType().Name ?? "null"}");
            }
        }

        private static void WriteHead(Stream s, byte major, ulong n)
        {
            var mt = (byte)(major << 5);
            if (n < 24)
            {
                s.WriteByte((byte)(mt | (byte)n));
            }
            else if (n <= byte.MaxValue)
            {
                s.WriteByte((byte)(mt | 24));
                s.WriteByte((byte)n);
            }
            else if (n <= ushort.MaxValue)
            {
                s.WriteByte((byte)(mt | 25));
                s.WriteByte((byte)(n >> 8));
                s.WriteByte((byte)n);
            }
            else if (n <= uint.MaxValue)
            {
                s.WriteByte((byte)(mt | 26));
                for (var shift = 24; shift >= 0; shift -= 8) s.WriteByte((byte)(n >> shift));
            }
            else
            {
                s.WriteByte((byte)(mt | 27));
                for (var shift = 56; shift >= 0; shift -= 8) s.WriteByte((byte)(n >> shift));
            }
        }
    }
}

public class CashuPaymentRequest
{
    public string? Id { get; set; }
    public long? Amount { get; set; }
    public string? Unit { get; set; }
    public bool? SingleUse { get; set; }
    public List<string> Mints { get; set; } = [];
    public string? Description { get; set; }
    public List<CashuTransport> Transports { get; set; } = [];
}

public record CashuTransport(string Type, string Target);
