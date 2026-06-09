namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public record ArkadePaymentMethodConfig(
    string WalletId,
    bool GeneratedByStore = false,
    bool AllowSubDustAmounts = false,
    bool BoardingEnabled = true,
    long MinBoardingAmountSats = ArkadePaymentMethodConfig.DefaultMinBoardingAmountSats,
    IReadOnlyList<TrackedArkadeAsset>? TrackedAssets = null)
{
    public const long P2trDustLimitSats = 330L;

    public const long DefaultMinBoardingAmountSats = 5000L;

    /// <summary>Tracked assets, never null (empty when none configured).</summary>
    public IReadOnlyList<TrackedArkadeAsset> Assets => TrackedAssets ?? [];
}

/// <summary>
/// A store-tracked Arkade asset the merchant accepts as payment. The rate is
/// merchant-declared via a free-form BTCPay rate-rule <see cref="RateScript"/>
/// (Arkade assets aren't exchange-listed). Ticker/Name/Decimals are cached from
/// the arkd indexer for display and settlement math. The asset is registered as
/// a BTCPay currency under <see cref="CurrencyCode"/>.
/// </summary>
public record TrackedArkadeAsset(
    string AssetId,
    string CurrencyCode,
    string? Ticker,
    string? Name,
    int Decimals,
    string RateScript,
    bool Enabled)
{
    /// <summary>Validates the tracked-asset config is internally consistent.</summary>
    public bool IsValid(out string? error)
    {
        if (string.IsNullOrWhiteSpace(AssetId)) { error = "An asset id is required."; return false; }
        if (string.IsNullOrWhiteSpace(CurrencyCode)) { error = "A currency code is required."; return false; }
        if (Decimals is < 0 or > 18) { error = "Decimals must be between 0 and 18."; return false; }
        if (string.IsNullOrWhiteSpace(RateScript)) { error = "A rate script is required."; return false; }
        error = null;
        return true;
    }
}
