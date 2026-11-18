using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.SoundboxCashu.Data;
using BTCPayServer.Plugins.SoundboxCashu.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.SoundboxCashu;

/// <summary>
/// Cashu ecash for the Lightning Soundbox -- as a separate plugin.
///
/// It has no dependency on the Lightning Soundbox plugin and never touches
/// its data. The only thing that connects them is BTCPay itself: received
/// ecash is settled by paying an ordinary Lightning invoice on the store, and
/// the Soundbox plugin announces settled Lightning invoices. Install this
/// next to it, uninstall it, or use it on a store with no soundbox at all.
/// </summary>
public class SoundboxCashuPlugin : BaseBTCPayServerPlugin
{
    // Must equal the assembly name -- see the Lightning Soundbox plugin for
    // the loader failure a mismatch causes.
    public override string Identifier => "BTCPayServer.Plugins.SoundboxCashu";
    public override string Name => "Soundbox Cashu";
    public override string Description =>
        "Accept Cashu ecash at the counter. Ecash is redeemed at your chosen mint and settled straight to your store's Lightning node.";

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.1.0" }
    ];

    public override void Execute(IServiceCollection services)
    {
        services.AddUIExtension("store-integrations-nav", "SoundboxCashuNav");

        services.AddSingleton<SoundboxCashuDbContextFactory>();
        services.AddStartupTask<SoundboxCashuMigrationRunner>();

        services.AddHttpClient(nameof(SoundboxCashu));
        services.AddSingleton<IStoreInvoiceCreator, StoreInvoiceCreator>();
        services.AddSingleton<CashuWalletService>();
        services.AddHostedService<CashuSettlementWorker>();

        base.Execute(services);
    }
}
