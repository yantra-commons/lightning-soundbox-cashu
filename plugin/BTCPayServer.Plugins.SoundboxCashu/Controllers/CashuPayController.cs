using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SoundboxCashu.Cashu;
using BTCPayServer.Plugins.SoundboxCashu.Data;
using BTCPayServer.Plugins.SoundboxCashu.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using NicolasDorier.RateLimits;

namespace BTCPayServer.Plugins.SoundboxCashu.Controllers;

/// <summary>
/// The NUT-18 "post" transport target: a payer's wallet sends its ecash here.
///
/// Anonymous by necessity -- the payer is a customer with no account -- and
/// CORS-open because web wallets (cashu.me and others) POST from the browser.
/// What it accepts is narrow: ecash for this store's configured mint only,
/// and nothing is credited until the mint itself has swapped it.
/// </summary>
[AllowAnonymous]
[ApiController]
[EnableCors(CorsPolicies.All)]
public class CashuPayController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // cashu-ts writes amounts as strings to keep them exact in JS.
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = false
    };

    private readonly CashuWalletService _wallet;

    public CashuPayController(CashuWalletService wallet) => _wallet = wallet;

    [HttpPost("~/plugins/sbcashu/{storeId}/pay")]
    [RequestSizeLimit(SoundboxCashuConstants.MaxPaymentBodyBytes)]
    [RateLimitsFilter(ZoneLimits.PublicInvoices, Scope = RateLimitsScope.RemoteAddress)]
    public async Task<IActionResult> Pay(string storeId, CancellationToken ct)
    {
        // Parsed by hand rather than [FromBody]: BTCPay's MVC is configured
        // for Newtonsoft, and these models use System.Text.Json attributes.
        // Wallets don't agree on Content-Type either, so don't gate on it.
        CashuPaymentPayload? payload;
        try
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            payload = JsonSerializer.Deserialize<CashuPaymentPayload>(body, JsonOpts);
        }
        catch (JsonException)
        {
            return Reply(400, new { error = "Malformed payment payload." });
        }

        if (payload is null)
            return Reply(400, new { error = "Empty payment payload." });

        var result = await _wallet.ReceiveAsync(storeId, payload, ct);
        return result.Success
            ? Reply(200, new { status = "paid", amount = result.NetAmount, unit = SoundboxCashuConstants.Unit })
            : Reply(result.HttpStatus, new { error = result.Message });
    }

    private ContentResult Reply(int status, object body) => new()
    {
        StatusCode = status,
        ContentType = "application/json",
        Content = JsonSerializer.Serialize(body)
    };
}
