using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public sealed class ArkEvmSettlementStore(StoreRepository repository, PaymentMethodHandlerDictionary handlers)
    : IArkEvmSettlementStore
{
    public ArkadePaymentMethodConfig? GetConfiguration(StoreData store) =>
        store.GetPaymentMethodConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, handlers);

    public async Task SaveAsync(StoreData store, ArkadePaymentMethodConfig configuration)
    {
        store.SetPaymentMethodConfig(handlers[ArkadePlugin.ArkadePaymentMethodId], configuration);
        await repository.UpdateStore(store);
    }
}
