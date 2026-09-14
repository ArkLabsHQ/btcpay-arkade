using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public interface IArkEvmSettlementStore
{
    ArkadePaymentMethodConfig? GetConfiguration(StoreData store);
    Task SaveAsync(StoreData store, ArkadePaymentMethodConfig configuration);
}
