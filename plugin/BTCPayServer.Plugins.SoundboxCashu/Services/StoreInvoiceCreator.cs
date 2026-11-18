using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Payments;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.SoundboxCashu.Services;

public record StoreLightningInvoice(string InvoiceId, string Bolt11);

/// <summary>
/// The one place this plugin touches BTCPay's invoicing. Behind an interface
/// so the wallet logic doesn't depend on BTCPay internals directly.
/// </summary>
public interface IStoreInvoiceCreator
{
    Task<StoreLightningInvoice> CreateLightningInvoiceAsync(
        string storeId, string serverUrl, long amountSat, string description, CancellationToken ct);
}

/// <summary>
/// Creates a real BTCPay invoice, Lightning-only, priced in sats.
///
/// This is the whole integration with the Lightning Soundbox: the mint pays
/// this invoice, BTCPay records a settled Lightning payment, and the Soundbox
/// plugin announces it exactly as it would a customer paying by Lightning.
/// Nothing in that plugin knows Cashu exists. It also means webhooks, the
/// invoice list and accounting all see Cashu sales as ordinary sales.
///
/// Uses UIInvoiceController.CreateInvoiceCoreRaw, the same entry point
/// BTCPay's own Point of Sale and Crowdfund apps use.
/// </summary>
public class StoreInvoiceCreator : IStoreInvoiceCreator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StoreRepository _stores;

    public StoreInvoiceCreator(IServiceScopeFactory scopeFactory, StoreRepository stores)
    {
        _scopeFactory = scopeFactory;
        _stores = stores;
    }

    public async Task<StoreLightningInvoice> CreateLightningInvoiceAsync(
        string storeId, string serverUrl, long amountSat, string description, CancellationToken ct)
    {
        var store = await _stores.FindStore(storeId)
                    ?? throw new InvalidOperationException($"Store {storeId} not found");

        var lnId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var request = new CreateInvoiceRequest
        {
            Amount = amountSat,
            Currency = "SATS",
            Metadata = new JObject
            {
                ["itemDesc"] = description,
                ["source"] = "soundbox-cashu"
            },
            Checkout = new InvoiceDataBase.CheckoutOptions
            {
                PaymentMethods = [lnId.ToString()],
                // The prompt must exist now -- we hand its BOLT11 to the mint.
                LazyPaymentMethods = false,
                Expiration = TimeSpan.FromMinutes(15)
            },
            AdditionalSearchTerms = ["cashu"]
        };

        await using var scope = _scopeFactory.CreateAsyncScope();
        var invoiceController = scope.ServiceProvider.GetRequiredService<UIInvoiceController>();
        var invoice = await invoiceController.CreateInvoiceCoreRaw(request, store, serverUrl, ["cashu"], ct);

        var bolt11 = invoice.GetPaymentPrompt(lnId)?.Destination;
        if (string.IsNullOrEmpty(bolt11))
            throw new InvalidOperationException(
                "BTCPay created the invoice without a Lightning payment request. " +
                "Check that this store has a working Lightning node configured.");

        return new StoreLightningInvoice(invoice.Id, bolt11);
    }
}
