using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.SoundboxCashu.Cashu;

// Wire models for the mint's HTTP API (NUT-00/01/02/03/05/07/08/09/23) and the
// NUT-18 payment payload. Property names are the protocol's own, which is why
// they are spelled out rather than derived from a naming policy -- "C", "B_"
// and "C_" fit no policy.

/// <summary>A spendable ecash note: (amount, keyset, secret, unblinded signature).</summary>
public class CashuProof
{
    [JsonPropertyName("amount")] public long Amount { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("secret")] public string Secret { get; set; } = string.Empty;
    [JsonPropertyName("C")] public string C { get; set; } = string.Empty;

    /// <summary>
    /// Spending-condition witness (NUT-10/11). Passed through untouched: if a
    /// customer sends locked proofs we cannot satisfy, the mint rejects the
    /// swap and the customer is told, which is the correct outcome.
    /// </summary>
    [JsonPropertyName("witness")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Witness { get; set; }
}

public class CashuBlindedMessage
{
    [JsonPropertyName("amount")] public long Amount { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("B_")] public string B_ { get; set; } = string.Empty;
}

public class CashuBlindSignature
{
    [JsonPropertyName("amount")] public long Amount { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("C_")] public string C_ { get; set; } = string.Empty;
}

public class CashuKeysetInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("unit")] public string Unit { get; set; } = string.Empty;
    [JsonPropertyName("active")] public bool Active { get; set; }

    /// <summary>Absent or null means zero, per NUT-02.</summary>
    [JsonPropertyName("input_fee_ppk")] public long? InputFeePpk { get; set; }
}

public class CashuKeysetsResponse
{
    [JsonPropertyName("keysets")] public List<CashuKeysetInfo> Keysets { get; set; } = [];
}

public class CashuKeyset
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("unit")] public string Unit { get; set; } = string.Empty;

    /// <summary>Amount (as a decimal string) -> compressed public key hex.</summary>
    [JsonPropertyName("keys")] public Dictionary<string, string> Keys { get; set; } = [];
}

public class CashuKeysResponse
{
    [JsonPropertyName("keysets")] public List<CashuKeyset> Keysets { get; set; } = [];
}

public class CashuSwapRequest
{
    [JsonPropertyName("inputs")] public List<CashuProof> Inputs { get; set; } = [];
    [JsonPropertyName("outputs")] public List<CashuBlindedMessage> Outputs { get; set; } = [];
}

public class CashuSwapResponse
{
    [JsonPropertyName("signatures")] public List<CashuBlindSignature> Signatures { get; set; } = [];
}

public class CashuMeltQuoteRequest
{
    [JsonPropertyName("request")] public string Request { get; set; } = string.Empty;
    [JsonPropertyName("unit")] public string Unit { get; set; } = "sat";
}

public class CashuMeltQuote
{
    [JsonPropertyName("quote")] public string Quote { get; set; } = string.Empty;
    [JsonPropertyName("amount")] public long Amount { get; set; }
    [JsonPropertyName("fee_reserve")] public long FeeReserve { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("expiry")] public long? Expiry { get; set; }
    [JsonPropertyName("payment_preimage")] public string? PaymentPreimage { get; set; }

    /// <summary>NUT-08 fee return, in the order of the blank outputs sent.</summary>
    [JsonPropertyName("change")] public List<CashuBlindSignature>? Change { get; set; }
}

public class CashuMeltRequest
{
    [JsonPropertyName("quote")] public string Quote { get; set; } = string.Empty;
    [JsonPropertyName("inputs")] public List<CashuProof> Inputs { get; set; } = [];
    [JsonPropertyName("outputs")] public List<CashuBlindedMessage> Outputs { get; set; } = [];
}

public class CashuRestoreRequest
{
    [JsonPropertyName("outputs")] public List<CashuBlindedMessage> Outputs { get; set; } = [];
}

public class CashuRestoreResponse
{
    [JsonPropertyName("outputs")] public List<CashuBlindedMessage> Outputs { get; set; } = [];
    [JsonPropertyName("signatures")] public List<CashuBlindSignature> Signatures { get; set; } = [];
}

public class CashuCheckStateRequest
{
    [JsonPropertyName("Ys")] public List<string> Ys { get; set; } = [];
}

public class CashuProofStateEntry
{
    [JsonPropertyName("Y")] public string Y { get; set; } = string.Empty;
    [JsonPropertyName("state")] public string State { get; set; } = string.Empty;
}

public class CashuCheckStateResponse
{
    [JsonPropertyName("states")] public List<CashuProofStateEntry> States { get; set; } = [];
}

public class CashuMintInfo
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
}

public class CashuMintError
{
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    [JsonPropertyName("code")] public int? Code { get; set; }
}

/// <summary>
/// What a payer's wallet POSTs to a NUT-18 "post" transport. Wallets differ
/// in which optional fields they send (cashu.me omits memo, for instance), so
/// only mint, unit and proofs are relied on.
/// </summary>
public class CashuPaymentPayload
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("memo")] public string? Memo { get; set; }
    [JsonPropertyName("mint")] public string? Mint { get; set; }
    [JsonPropertyName("unit")] public string? Unit { get; set; }
    [JsonPropertyName("proofs")] public List<CashuProof>? Proofs { get; set; }
}
