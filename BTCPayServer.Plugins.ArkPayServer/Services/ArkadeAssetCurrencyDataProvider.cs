using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Stores;

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
/// </summary>
public class ArkadeAssetCurrencyDataProvider(
    StoreRepository stores,
    PaymentMethodHandlerDictionary handlers) : CurrencyDataProvider
{
    public async Task<CurrencyData[]> LoadCurrencyData(CancellationToken cancellationToken)
    {
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
