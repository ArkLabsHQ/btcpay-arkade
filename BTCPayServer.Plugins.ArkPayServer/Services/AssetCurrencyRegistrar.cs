using BTCPayServer.Services.Rates;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Refreshes BTCPay's currency table after a tracked-asset CRUD operation so a
/// newly-added (or removed) asset code is recognised immediately, without a
/// process restart. Wraps <see cref="CurrencyNameTable.ReloadCurrencyData"/>,
/// which re-runs every <see cref="CurrencyDataProvider"/> — including
/// <see cref="ArkadeAssetCurrencyDataProvider"/>.
/// </summary>
public class AssetCurrencyRegistrar(CurrencyNameTable currencies)
{
    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        currencies.ReloadCurrencyData(cancellationToken);
}
