namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <param name="OnchainSwapEnabled">
/// Whether to offer the fast onchain path — a swap through a solver — when one quotes the pair.
/// </param>
public record ArkadePaymentMethodConfig(
    string WalletId,
    bool GeneratedByStore = false,
    bool AllowSubDustAmounts = false,
    bool BoardingEnabled = true,
    long MinBoardingAmountSats = ArkadePaymentMethodConfig.DefaultMinBoardingAmountSats,
    bool OnchainSwapEnabled = false)
{
    public const long P2trDustLimitSats = 330L;

    public const long DefaultMinBoardingAmountSats = 5000L;
}