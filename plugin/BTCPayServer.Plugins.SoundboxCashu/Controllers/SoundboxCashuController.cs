using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Plugins.SoundboxCashu.Cashu;
using BTCPayServer.Plugins.SoundboxCashu.Data;
using BTCPayServer.Plugins.SoundboxCashu.Models;
using BTCPayServer.Plugins.SoundboxCashu.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.SoundboxCashu.Controllers;

[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class SoundboxCashuController : Controller
{
    private readonly SoundboxCashuDbContextFactory _dbFactory;
    private readonly CashuWalletService _wallet;

    public SoundboxCashuController(SoundboxCashuDbContextFactory dbFactory, CashuWalletService wallet)
    {
        _dbFactory = dbFactory;
        _wallet = wallet;
    }

    [HttpGet("~/plugins/sbcashu/{storeId}")]
    public async Task<IActionResult> Index(string storeId, CancellationToken ct)
    {
        await using var db = _dbFactory.CreateContext();
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.StoreId == storeId, ct);

        var balances = await db.Proofs
            .Where(p => p.StoreId == storeId && (p.State == CashuProofState.Unspent || p.State == CashuProofState.Reserved))
            .GroupBy(p => p.MintUrl)
            .Select(g => new MintBalanceViewModel
            {
                MintUrl = g.Key,
                Spendable = g.Where(p => p.State == CashuProofState.Unspent).Sum(p => p.Amount),
                InFlight = g.Where(p => p.State == CashuProofState.Reserved).Sum(p => p.Amount)
            })
            .ToListAsync(ct);

        var ops = await db.Operations
            .Where(o => o.StoreId == storeId)
            .OrderByDescending(o => o.CreatedAt)
            .Take(50)
            .AsNoTracking()
            .ToListAsync(ct);

        var vm = new SoundboxCashuViewModel
        {
            StoreId = storeId,
            Enabled = settings?.Enabled ?? false,
            MintUrl = settings?.MintUrl ?? string.Empty,
            Balances = balances,
            Operations = ops,
            ExportedToken = TempData["ExportedToken"] as string
        };

        if (settings is { Enabled: true } && !string.IsNullOrEmpty(settings.MintUrl))
        {
            vm.PaymentRequest = CashuWalletService.BuildPaymentRequest(settings);
            vm.PayEndpoint = CashuWalletService.PayEndpoint(settings);
        }

        return View(vm);
    }

    [HttpPost("~/plugins/sbcashu/{storeId}/settings")]
    public async Task<IActionResult> SaveSettings(string storeId, bool enabled, string? mintUrl, CancellationToken ct)
    {
        var normalized = string.IsNullOrWhiteSpace(mintUrl) ? string.Empty : CashuMintClient.NormalizeMintUrl(mintUrl);

        if (enabled)
        {
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                TempData["ErrorMessage"] = "Enter the mint's https:// URL.";
                return RedirectToAction(nameof(Index), new { storeId });
            }

            // Prove the mint is usable before a QR goes on a counter: it must
            // answer, and it must have an active sat keyset to issue change in.
            try
            {
                var keysets = await _wallet.Mint(normalized).GetKeysetsAsync(ct);
                if (CashuWalletOps.PickActiveKeyset(keysets, SoundboxCashuConstants.Unit) is null)
                {
                    TempData["ErrorMessage"] = "That mint has no active sat keyset.";
                    return RedirectToAction(nameof(Index), new { storeId });
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                TempData["ErrorMessage"] = $"Could not reach that mint: {ex.Message}";
                return RedirectToAction(nameof(Index), new { storeId });
            }
        }

        await using var db = _dbFactory.CreateContext();
        var settings = await db.Settings.FirstOrDefaultAsync(s => s.StoreId == storeId, ct);
        if (settings is null)
        {
            settings = new CashuStoreSettings { StoreId = storeId };
            db.Settings.Add(settings);
        }
        settings.Enabled = enabled;
        if (!string.IsNullOrEmpty(normalized)) settings.MintUrl = normalized;
        settings.ServerUrl = Request.GetAbsoluteRoot();
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        TempData["SuccessMessage"] = enabled
            ? "Cashu payments are on. Put the payment request below on your Soundbox."
            : "Cashu payments are off.";
        return RedirectToAction(nameof(Index), new { storeId });
    }

    [HttpPost("~/plugins/sbcashu/{storeId}/settle")]
    public async Task<IActionResult> SettleNow(string storeId, CancellationToken ct)
    {
        await _wallet.SettleStoreAsync(storeId, ct);
        TempData["SuccessMessage"] = "Settlement attempted. See the activity list for the result.";
        return RedirectToAction(nameof(Index), new { storeId });
    }

    [HttpPost("~/plugins/sbcashu/{storeId}/export")]
    public async Task<IActionResult> Export(string storeId, string mintUrl, CancellationToken ct)
    {
        var op = await _wallet.ExportAsync(storeId, mintUrl, ct);
        if (op is null)
        {
            TempData["ErrorMessage"] = "No spendable ecash at that mint.";
        }
        else
        {
            TempData["ExportedToken"] = op.Token;
            TempData["SuccessMessage"] =
                $"Exported {op.Amount} sat. Paste the token below into your own Cashu wallet now -- " +
                "it is bearer money. It also stays in the activity list.";
        }
        return RedirectToAction(nameof(Index), new { storeId });
    }
}
