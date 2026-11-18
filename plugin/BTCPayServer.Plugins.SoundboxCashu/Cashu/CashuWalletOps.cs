using System;
using System.Collections.Generic;
using System.Linq;

namespace BTCPayServer.Plugins.SoundboxCashu.Cashu;

/// <summary>
/// One blinded output we are about to ask the mint to sign, with everything
/// needed to turn the mint's answer into a spendable proof.
///
/// Callers persist these BEFORE sending the request. If the connection drops
/// after the mint has signed, the secret and blinding factor are the only way
/// to recover the money (via NUT-09 restore) -- lose them and the ecash is
/// gone even though the mint did its part.
/// </summary>
public class PreparedOutput
{
    public string KeysetId { get; init; } = string.Empty;

    /// <summary>Requested amount. Zero for a NUT-08 blank (fee-return) output.</summary>
    public long Amount { get; init; }

    public string Secret { get; init; } = string.Empty;
    public string BlindingFactor { get; init; } = string.Empty;
    public string B_ { get; init; } = string.Empty;

    public CashuBlindedMessage ToBlindedMessage() => new() { Amount = Amount, Id = KeysetId, B_ = B_ };
}

/// <summary>
/// Stateless wallet operations. No I/O, no storage -- the plugin's services
/// own both -- so this is what the live-mint tests exercise directly.
/// </summary>
public static class CashuWalletOps
{
    public static List<PreparedOutput> PrepareOutputs(string keysetId, IEnumerable<long> amounts) =>
        amounts.Select(a => Prepare(keysetId, a)).ToList();

    public static List<PreparedOutput> PrepareBlankOutputs(string keysetId, int count) =>
        Enumerable.Range(0, count).Select(_ => Prepare(keysetId, 0)).ToList();

    private static PreparedOutput Prepare(string keysetId, long amount)
    {
        var secret = CashuCrypto.NewSecret();
        var r = CashuCrypto.NewBlindingFactor();
        var b = CashuCrypto.Blind(CashuCrypto.HashToCurve(secret), r);
        return new PreparedOutput
        {
            KeysetId = keysetId,
            Amount = amount,
            Secret = secret,
            BlindingFactor = CashuCrypto.ToHex(r),
            B_ = CashuCrypto.ToHex(b)
        };
    }

    /// <summary>
    /// Turns one blind signature into a proof. The amount and keyset come from
    /// the SIGNATURE, not the request: for a blank output the mint decides the
    /// amount, and the unblinding key has to be the one for that amount.
    /// </summary>
    public static CashuProof Unblind(PreparedOutput output, CashuBlindSignature signature, IReadOnlyDictionary<long, string> keys)
    {
        if (!string.Equals(signature.Id, output.KeysetId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Mint signed with keyset {signature.Id}, expected {output.KeysetId}");
        if (output.Amount != 0 && signature.Amount != output.Amount)
            throw new InvalidOperationException($"Mint signed amount {signature.Amount}, expected {output.Amount}");
        if (!keys.TryGetValue(signature.Amount, out var keyHex))
            throw new InvalidOperationException($"Keyset {signature.Id} has no key for amount {signature.Amount}");

        var c = CashuCrypto.Unblind(
            CashuCrypto.PubKeyFromHex(signature.C_),
            CashuCrypto.PrivKeyFromHex(output.BlindingFactor),
            CashuCrypto.PubKeyFromHex(keyHex));

        return new CashuProof
        {
            Amount = signature.Amount,
            Id = signature.Id,
            Secret = output.Secret,
            C = CashuCrypto.ToHex(c)
        };
    }

    /// <summary>Y = hash_to_curve(secret), the identifier NUT-07 uses for a proof.</summary>
    public static string Y(string secret) => CashuCrypto.ToHex(CashuCrypto.HashToCurve(secret));

    /// <summary>
    /// Picks proofs worth at least <paramref name="target"/> after their own
    /// input fees. Largest first, which keeps the input count -- and so the
    /// fee -- low. Returns null if the balance cannot cover it.
    /// </summary>
    public static List<T>? SelectProofs<T>(
        IEnumerable<T> available, Func<T, long> amount, Func<T, string> keysetId,
        long target, IReadOnlyDictionary<string, long> feePpkByKeyset)
    {
        var chosen = new List<T>();
        long sum = 0;
        foreach (var p in available.OrderByDescending(amount))
        {
            if (sum - CashuMintClient.InputFee(chosen.Select(keysetId), feePpkByKeyset) >= target) break;
            chosen.Add(p);
            sum += amount(p);
        }
        return sum - CashuMintClient.InputFee(chosen.Select(keysetId), feePpkByKeyset) >= target ? chosen : null;
    }

    /// <summary>
    /// The keyset new outputs should go to: an active one in the right unit,
    /// preferring the cheapest to spend from later.
    /// </summary>
    public static CashuKeysetInfo? PickActiveKeyset(IEnumerable<CashuKeysetInfo> keysets, string unit) =>
        keysets
            .Where(k => k.Active && string.Equals(k.Unit, unit, StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k.InputFeePpk ?? 0)
            .FirstOrDefault();
}
