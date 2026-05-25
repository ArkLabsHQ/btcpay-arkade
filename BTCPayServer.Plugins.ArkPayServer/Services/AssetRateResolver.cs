using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// The Arkade-asset amount a customer must pay to settle an invoice.
/// </summary>
/// <param name="BaseUnits">
/// Raw amount carried on the asset VTXO (what coin selection / payment
/// matching compares against). Always rounded up so the merchant is never
/// underpaid, and never below one base unit.
/// </param>
/// <param name="DisplayUnits">
/// <see cref="BaseUnits"/> expressed in whole asset units
/// (BaseUnits / 10^decimals) — the value the customer sees.
/// </param>
/// <param name="FormattedAmount">
/// <see cref="DisplayUnits"/> rendered to the asset's declared decimals.
/// </param>
/// <param name="RateDescription">
/// Human-readable account of how the amount was derived (for invoice logs).
/// </param>
public record AssetAmountDue(
    ulong BaseUnits,
    decimal DisplayUnits,
    string FormattedAmount,
    string RateDescription);

/// <summary>
/// Resolves how many units of a merchant-tracked Arkade asset settle an
/// invoice, given the invoice's bitcoin amount due.
/// <para>
/// Arkade assets aren't quoted on exchanges, so the price is merchant-declared
/// via a free-form BTCPay rate-rule <see cref="TrackedArkadeAsset.RateScript"/>
/// (e.g. <c>USDARK_USD = 1;</c> or <c>MYA_BTC = 100000;</c>). The script is
/// combined with the store's own rate rules and evaluated through BTCPay's
/// <see cref="RateFetcher"/> for the <c>BTC → asset</c> pair — so store
/// spread/fallback/rate-source config keeps applying, and chained legs (the
/// asset priced in a real currency, then that currency in BTC) resolve. The
/// store's global <c>RateScript</c> is never mutated.
/// </para>
/// </summary>
public class AssetRateResolver(RateFetcher rateFetcher, DefaultRulesCollection defaultRules)
{
    /// <summary>
    /// Computes the asset amount due for an invoice.
    /// </summary>
    /// <param name="store">The invoice's store (for its rate rules).</param>
    /// <param name="asset">The tracked asset to price in.</param>
    /// <param name="dueSats">Invoice amount due, in satoshis (BTC leg).</param>
    /// <exception cref="InvalidOperationException">
    /// The config is invalid, the rate script doesn't compile, or the rate
    /// could not be evaluated. The caller translates this into the invoice
    /// simply not offering the asset (never a hard failure).
    /// </exception>
    public async Task<AssetAmountDue> ResolveAsync(
        StoreData store, TrackedArkadeAsset asset, long dueSats, CancellationToken cancellationToken)
    {
        if (!asset.IsValid(out var configError))
            throw new InvalidOperationException($"Invalid tracked asset: {configError}");
        if (dueSats <= 0)
            throw new InvalidOperationException("Invoice amount due must be positive to price an asset.");

        if (!RateRules.TryParse(asset.RateScript, out var assetRules, out var parseErrors))
            throw new InvalidOperationException(
                $"Invalid rate script for {asset.CurrencyCode}: {string.Join("; ", parseErrors)}");

        // Combine the asset's rule into the store's existing rules — both the
        // primary and fallback legs — so chained legs resolve (e.g. the asset
        // priced in USD, then USD→BTC via the store's configured source) and the
        // store's primary/fallback rate-source order keeps applying. The store's
        // persisted RateScript is NOT modified.
        var storeRules = store.GetStoreBlob().GetRateRules(defaultRules);
        var combined = new RateRulesCollection(
            RateRules.Combine([assetRules, storeRules.Primary]),
            storeRules.Fallback is null
                ? null
                : RateRules.Combine([assetRules, storeRules.Fallback]));

        var pair = new CurrencyPair("BTC", asset.CurrencyCode); // units of asset per 1 BTC
        var rate = await rateFetcher.FetchRate(pair, combined, new StoreIdRateContext(store.Id), cancellationToken);
        if (rate.BidAsk is null || rate.Errors is { Count: > 0 })
            throw new InvalidOperationException(
                $"Unable to evaluate rate for {pair}" +
                (rate.Errors is { Count: > 0 } ? $" ({string.Join(", ", rate.Errors)})" : ""));

        var dueBtc = dueSats / 100_000_000m;
        var unitsPerBtc = rate.BidAsk.Center;
        var displayUnits = dueBtc * unitsPerBtc;

        var (baseUnits, actualDisplay) = AssetAmount.BaseUnitsDue(displayUnits, asset.Decimals);
        var rateDescription =
            $"{pair} = {unitsPerBtc}; {dueBtc} BTC = {displayUnits} {asset.CurrencyCode} " +
            $"→ {AssetAmount.Format(baseUnits, asset.Decimals)} {asset.CurrencyCode}";

        return new AssetAmountDue(baseUnits, actualDisplay,
            AssetAmount.Format(baseUnits, asset.Decimals), rateDescription);
    }
}
