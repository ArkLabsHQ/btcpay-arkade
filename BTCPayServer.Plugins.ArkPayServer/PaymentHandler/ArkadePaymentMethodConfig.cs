namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public record ArkadePaymentMethodConfig(
    string WalletId,
    bool GeneratedByStore = false,
    bool AllowSubDustAmounts = false,
    bool BoardingEnabled = true,
    long MinBoardingAmountSats = ArkadePaymentMethodConfig.DefaultMinBoardingAmountSats,
    ArkadeAssetAcceptance? AssetAcceptance = null)
{
    public const long P2trDustLimitSats = 330L;

    public const long DefaultMinBoardingAmountSats = 5000L;
}

/// <summary>
/// How a merchant prices an accepted Arkade asset. Arkade assets aren't on
/// exchanges, so the price is merchant-declared. Two models:
/// <list type="bullet">
/// <item><see cref="FixedReferenceCurrency"/> — 1 asset unit costs
/// <c>PricePerUnit</c> of <c>ReferenceCurrency</c> (e.g. a USD-pegged
/// stablecoin: 1 unit = 1 USD). The invoice→reference-currency leg goes
/// through BTCPay's existing rate pipeline.</item>
/// <item><see cref="SatsPerUnit"/> — 1 asset unit costs <c>PricePerUnit</c>
/// satoshis. Only BTCPay's BTC rate for the invoice currency is needed.</item>
/// </list>
/// </summary>
public enum AssetRateMode
{
    FixedReferenceCurrency,
    SatsPerUnit
}

/// <summary>
/// Store-scoped configuration making the Arkade payment method settle an
/// invoice in a specific Arkade asset at a merchant-declared rate.
/// Null on <see cref="ArkadePaymentMethodConfig"/> = asset acceptance off
/// (BTC-VTXO behaviour unchanged).
/// </summary>
public record ArkadeAssetAcceptance(
    string AssetId,
    AssetRateMode RateMode,
    decimal PricePerUnit,
    string? ReferenceCurrency = null)
{
    /// <summary>
    /// Validates the acceptance config is internally consistent.
    /// </summary>
    public bool IsValid(out string? error)
    {
        if (string.IsNullOrWhiteSpace(AssetId))
        {
            error = "An asset id is required.";
            return false;
        }
        if (PricePerUnit <= 0m)
        {
            error = "Price per unit must be greater than zero.";
            return false;
        }
        if (RateMode == AssetRateMode.FixedReferenceCurrency &&
            string.IsNullOrWhiteSpace(ReferenceCurrency))
        {
            error = "A reference currency is required for the fixed-currency rate model.";
            return false;
        }
        error = null;
        return true;
    }
}
