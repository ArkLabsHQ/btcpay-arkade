using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Exposes every store's tracked Arkade assets to BTCPay's
/// <see cref="CurrencyNameTable"/> as first-class currencies, so a tracked
/// asset's code (e.g. <c>USDARK</c>) is a valid pricing/display currency
/// without shipping it in BTCPay's static currency list.
/// <para>
/// Codes are unique within a store (enforced on CRUD); across stores the
/// first occurrence wins (<see cref="Dictionary{TKey,TValue}.TryAdd"/>), which
/// is harmless because each store prices against its own asset record.
/// </para>
/// <para>
/// <b>DI note:</b> dependencies are resolved lazily through
/// <see cref="IServiceProvider"/> inside <see cref="LoadCurrencyData"/> — NOT
/// injected into the constructor. <see cref="CurrencyNameTable"/> takes
/// <c>IEnumerable&lt;CurrencyDataProvider&gt;</c>, so injecting
/// <see cref="PaymentMethodHandlerDictionary"/> here would force the whole
/// payment-handler graph to build while <see cref="CurrencyNameTable"/> is
/// still constructing — and core handlers depend back on
/// <see cref="CurrencyNameTable"/>, forming a DI cycle that hangs startup.
/// Resolving at load time (after the table is constructed) sidesteps it, the
/// same way <c>ArkadeCheckoutModelExtension</c> does for its own cycle.
/// </para>
/// </summary>
public class ArkadeAssetCurrencyDataProvider(IServiceProvider serviceProvider) : CurrencyDataProvider
{
    public async Task<CurrencyData[]> LoadCurrencyData(CancellationToken cancellationToken)
    {
        var stores = serviceProvider.GetRequiredService<StoreRepository>();
        var handlers = serviceProvider.GetRequiredService<PaymentMethodHandlerDictionary>();

        var seen = new Dictionary<string, CurrencyData>(StringComparer.OrdinalIgnoreCase);
        foreach (var store in await stores.GetStores())
        {
            var cfg = store.GetPaymentMethodConfig<ArkadePaymentMethodConfig>(
                ArkadePlugin.ArkadePaymentMethodId, handlers);
            if (cfg is null)
                continue;

            foreach (var asset in cfg.Assets)
            {
                if (string.IsNullOrWhiteSpace(asset.CurrencyCode))
                    continue;

                seen.TryAdd(asset.CurrencyCode, new CurrencyData
                {
                    Code = asset.CurrencyCode,
                    Name = asset.Name ?? asset.Ticker ?? asset.CurrencyCode,
                    Divisibility = asset.Decimals,
                    Symbol = asset.Ticker ?? asset.CurrencyCode,
                    Crypto = true,
                });
            }
        }

        return seen.Values.ToArray();
    }
}
