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
/// Resolves how many units of a merchant-accepted Arkade asset settle an
/// invoice, given the invoice's bitcoin amount due.
/// <para>
/// Arkade assets aren't quoted on exchanges, so the price is
/// merchant-declared (<see cref="ArkadeAssetAcceptance"/>). Two models:
/// <list type="bullet">
/// <item><see cref="AssetRateMode.SatsPerUnit"/> — self-contained: one
/// asset unit is worth <c>PricePerUnit</c> satoshis, so the BTC amount due
/// divides straight through.</item>
/// <item><see cref="AssetRateMode.FixedReferenceCurrency"/> — one asset
/// unit is worth <c>PricePerUnit</c> of a real currency; the BTC→reference
/// leg goes through BTCPay's own rate pipeline (same path payouts use), so
/// store spread/fallback/rate-source config all keep applying.</item>
/// </list>
/// </para>
/// </summary>
public class AssetRateResolver(RateFetcher rateFetcher, DefaultRulesCollection defaultRules)
{
    /// <summary>
    /// Computes the asset amount due for an invoice.
    /// </summary>
    /// <param name="store">The invoice's store (for its rate rules).</param>
    /// <param name="acceptance">The store's asset-acceptance config.</param>
    /// <param name="dueSats">Invoice amount due, in satoshis (BTC leg).</param>
    /// <param name="assetDecimals">Asset decimals from indexer metadata.</param>
    /// <exception cref="InvalidOperationException">
    /// The reference-currency rate could not be fetched, or the config is
    /// internally inconsistent. The caller translates this into the invoice
    /// simply not offering the asset (never a hard failure).
    /// </exception>
    public async Task<AssetAmountDue> ResolveAsync(
        StoreData store,
        ArkadeAssetAcceptance acceptance,
        long dueSats,
        int assetDecimals,
        CancellationToken cancellationToken)
    {
        if (!acceptance.IsValid(out var configError))
            throw new InvalidOperationException($"Invalid asset acceptance config: {configError}");
        if (dueSats <= 0)
            throw new InvalidOperationException("Invoice amount due must be positive to price an asset.");

        var dueBtc = dueSats / 100_000_000m;

        decimal displayUnits;
        string rateDescription;

        switch (acceptance.RateMode)
        {
            case AssetRateMode.SatsPerUnit:
            {
                // 1 asset unit = PricePerUnit sats. No external rate needed.
                displayUnits = dueSats / acceptance.PricePerUnit;
                rateDescription =
                    $"{acceptance.PricePerUnit} sats/unit → {dueSats} sats = {displayUnits} units";
                break;
            }
            case AssetRateMode.FixedReferenceCurrency:
            {
                // 1 asset unit = PricePerUnit of ReferenceCurrency. Convert
                // the BTC amount due into the reference currency through the
                // store's configured rate pipeline, then divide by the
                // merchant's per-unit price.
                var pair = new CurrencyPair("BTC", acceptance.ReferenceCurrency!);
                var storeBlob = store.GetStoreBlob();
                var rule = storeBlob.GetRateRules(defaultRules).GetRuleFor(pair);
                var rate = await rateFetcher.FetchRate(
                    rule, new StoreIdRateContext(store.Id), cancellationToken);

                if (rate.BidAsk is null || (rate.Errors is { Count: > 0 }))
                    throw new InvalidOperationException(
                        $"Unable to fetch {pair} rate for asset pricing" +
                        (rate.Errors is { Count: > 0 }
                            ? $" ({string.Join(", ", rate.Errors)})"
                            : ""));

                var btcToRef = rate.BidAsk.Center;
                var dueInRef = dueBtc * btcToRef;
                displayUnits = dueInRef / acceptance.PricePerUnit;
                rateDescription =
                    $"1 BTC = {btcToRef} {acceptance.ReferenceCurrency}; " +
                    $"{dueBtc} BTC = {dueInRef} {acceptance.ReferenceCurrency}; " +
                    $"@ {acceptance.PricePerUnit} {acceptance.ReferenceCurrency}/unit = {displayUnits} units";
                break;
            }
            default:
                throw new InvalidOperationException(
                    $"Unsupported asset rate mode {acceptance.RateMode}");
        }

        // Convert whole units → raw base units. Round UP and clamp to at
        // least one base unit: the merchant must never be underpaid, and a
        // zero-amount asset output is meaningless.
        var scale = Pow10(assetDecimals);
        var baseUnitsExact = Math.Ceiling(displayUnits * scale);
        if (baseUnitsExact < 1m)
            baseUnitsExact = 1m;
        var baseUnits = (ulong)baseUnitsExact;

        var actualDisplay = baseUnitsExact / scale;
        // Up to `decimals` fractional digits, no trailing zeros, no trailing
        // dot, and always at least the integer part (so 100 → "100", not
        // "1"; 0.000001 → "0.000001").
        var formatted = assetDecimals == 0
            ? baseUnits.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : actualDisplay.ToString(
                "0." + new string('#', Math.Clamp(assetDecimals, 1, 18)),
                System.Globalization.CultureInfo.InvariantCulture);

        return new AssetAmountDue(baseUnits, actualDisplay, formatted, rateDescription);
    }

    /// <summary>10^exp as a decimal (exp clamped to a sane asset range).</summary>
    private static decimal Pow10(int exp)
    {
        exp = Math.Clamp(exp, 0, 18);
        decimal result = 1m;
        for (var i = 0; i < exp; i++)
            result *= 10m;
        return result;
    }
}
