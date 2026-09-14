using BTCPayServer.Data;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>Loads authoritative store and invoice context without resolving payment handlers.</summary>
public interface IArkCompositionContextSource
{
    Task<StoreData?> FindStoreAsync(string storeId);
    Task<InvoiceEntity?> FindInvoiceAsync(string invoiceId);
}

public sealed class ArkCompositionContextSource(StoreRepository stores, InvoiceRepository invoices) : IArkCompositionContextSource
{
    public Task<StoreData?> FindStoreAsync(string storeId) => stores.FindStore(storeId);
    public Task<InvoiceEntity?> FindInvoiceAsync(string invoiceId) => invoices.GetInvoice(invoiceId);
}
