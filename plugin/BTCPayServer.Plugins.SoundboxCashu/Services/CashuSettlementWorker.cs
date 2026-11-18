using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SoundboxCashu.Services;

/// <summary>
/// Drives settlement and recovery in the background.
///
/// Settlement is requested the moment a payment is received, so the usual
/// path from "customer's wallet POSTs ecash" to "BTCPay invoice paid" is a
/// couple of seconds. The periodic pass is the safety net: retries (with
/// backoff) for settlements that failed, and recovery for anything a crash
/// or dropped connection left half-done.
/// </summary>
public class CashuSettlementWorker : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);

    private readonly CashuWalletService _wallet;
    private readonly ILogger<CashuSettlementWorker> _logger;

    public CashuSettlementWorker(CashuWalletService wallet, ILogger<CashuSettlementWorker> logger)
    {
        _wallet = wallet;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(ConsumeRequestsAsync(stoppingToken), SweepAsync(stoppingToken));

    private async Task ConsumeRequestsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var storeId in _wallet.SettleRequests.ReadAllAsync(ct))
            {
                try
                {
                    await _wallet.SettleStoreAsync(storeId, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Cashu: settlement for store {Store} failed", storeId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _wallet.RecoverAsync(ct);
                foreach (var storeId in await _wallet.StoresDueForSettlementAsync(ct))
                    _wallet.RequestSettlement(storeId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cashu: sweep failed");
            }

            try { await timer.WaitForNextTickAsync(ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}
