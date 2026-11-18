using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NBitcoin.Secp256k1;
using SHA256 = System.Security.Cryptography.SHA256;

namespace BTCPayServer.Plugins.SoundboxCashu.Cashu;

/// <summary>
/// The wallet half of Cashu's blind Diffie-Hellman key exchange (NUT-00).
///
/// The merchant's server acts as a Cashu wallet: when a customer's ecash
/// arrives it is immediately swapped at the mint for fresh proofs that only
/// this server knows the secrets of. That needs exactly three operations,
/// all of them here:
///
///   Y  = hash_to_curve(secret)
///   B_ = Y + rG                (blind; sent to the mint)
///   C  = C_ - rK               (unblind the mint's signature C_ = kB_)
///
/// Implemented on NBitcoin.Secp256k1 -- the same pure-C# libsecp256k1 port
/// that NBitcoin itself embeds -- so no native library ships with the plugin.
/// Checked against the official NUT-00 test vectors in the test project.
/// </summary>
public static class CashuCrypto
{
    private static readonly byte[] DomainSeparator = Encoding.ASCII.GetBytes("Secp256k1_HashToCurve_Cashu_");

    /// <summary>
    /// Y = PublicKey('02' || SHA256(msg_hash || counter)), with
    /// msg_hash = SHA256(DOMAIN_SEPARATOR || message) and counter a
    /// little-endian uint32 incremented from 0 until the candidate is a valid
    /// point. Half of all x coordinates are on the curve, so in practice this
    /// takes one or two iterations; the bound exists only so a bug can never
    /// spin forever.
    /// </summary>
    public static ECPubKey HashToCurve(ReadOnlySpan<byte> message)
    {
        var prefixed = new byte[DomainSeparator.Length + message.Length];
        DomainSeparator.CopyTo(prefixed, 0);
        message.CopyTo(prefixed.AsSpan(DomainSeparator.Length));

        Span<byte> toHash = stackalloc byte[36];
        SHA256.HashData(prefixed, toHash[..32]);

        Span<byte> candidate = stackalloc byte[33];
        candidate[0] = 0x02;

        for (uint counter = 0; counter < 1u << 16; counter++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(toHash[32..], counter);
            SHA256.HashData(toHash, candidate[1..]);
            if (ECPubKey.TryCreate(candidate, Context.Instance, out _, out var point) && point is not null)
                return point;
        }

        throw new InvalidOperationException("hash_to_curve found no valid point");
    }

    /// <summary>A Cashu secret is hashed as its UTF-8 bytes.</summary>
    public static ECPubKey HashToCurve(string secret) => HashToCurve(Encoding.UTF8.GetBytes(secret));

    /// <summary>B_ = Y + rG.</summary>
    public static ECPubKey Blind(ECPubKey y, ECPrivKey r) => Add(y, r.CreatePubKey());

    /// <summary>C = C_ - rK, where K is the mint's public key for the amount.</summary>
    public static ECPubKey Unblind(ECPubKey blindSignature, ECPrivKey r, ECPubKey mintKey) =>
        Add(blindSignature, mintKey.GetSharedPubkey(r).Negate());

    /// <summary>A fresh blinding factor: a uniformly random valid scalar.</summary>
    public static ECPrivKey NewBlindingFactor()
    {
        Span<byte> buf = stackalloc byte[32];
        while (true)
        {
            RandomNumberGenerator.Fill(buf);
            if (ECPrivKey.TryCreate(buf, Context.Instance, out var key) && key is not null)
                return key;
        }
    }

    /// <summary>
    /// A fresh secret: 32 random bytes as 64 lowercase hex characters, which
    /// is the form NUT-00 recommends.
    /// </summary>
    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static ECPrivKey PrivKeyFromHex(string hex) => ECPrivKey.Create(Convert.FromHexString(hex), Context.Instance);

    public static ECPubKey PubKeyFromHex(string hex) => ECPubKey.Create(Convert.FromHexString(hex), Context.Instance);

    public static string ToHex(ECPubKey key)
    {
        Span<byte> buf = stackalloc byte[33];
        key.WriteToSpan(true, buf, out _);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }

    public static string ToHex(ECPrivKey key)
    {
        Span<byte> buf = stackalloc byte[32];
        key.WriteToSpan(buf);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }

    private static ECPubKey Add(ECPubKey a, ECPubKey b)
    {
        var sum = a.Q.ToGroupElementJacobian().AddVariable(b.Q).ToGroupElement();
        if (sum.IsInfinity)
            throw new InvalidOperationException("Point addition produced the point at infinity");
        return new ECPubKey(sum, Context.Instance);
    }
}
