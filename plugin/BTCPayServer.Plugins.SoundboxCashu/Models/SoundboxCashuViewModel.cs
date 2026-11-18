using System.Collections.Generic;
using BTCPayServer.Plugins.SoundboxCashu.Data;

namespace BTCPayServer.Plugins.SoundboxCashu.Models;

public class SoundboxCashuViewModel
{
    public string StoreId { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string MintUrl { get; set; } = string.Empty;

    /// <summary>The creqA string to display. Null until enabled with a mint.</summary>
    public string? PaymentRequest { get; set; }
    public string? PayEndpoint { get; set; }

    public List<MintBalanceViewModel> Balances { get; set; } = [];
    public List<CashuOperation> Operations { get; set; } = [];

    /// <summary>Set right after an export, so the token is shown once prominently.</summary>
    public string? ExportedToken { get; set; }

    /// <summary>
    /// The Soundbox draws QR codes at two pixels per module in a 110 px box,
    /// so it can show up to version 9 (53 modules) at error level L: 230 bytes
    /// of text. Longer still renders, but runs slightly past the box edge.
    /// </summary>
    public const int SoundboxComfortableQrBytes = 230;
}

public class MintBalanceViewModel
{
    public string MintUrl { get; set; } = string.Empty;
    public long Spendable { get; set; }
    public long InFlight { get; set; }
}
