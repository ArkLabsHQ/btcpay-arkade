using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using NArk.Swaps.Models;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public record ArkadePaymentMethodConfig(
    string WalletId,
    bool GeneratedByStore = false,
    bool AllowSubDustAmounts = false,
    bool BoardingEnabled = true,
    long MinBoardingAmountSats = ArkadePaymentMethodConfig.DefaultMinBoardingAmountSats,
    [property: JsonConverter(typeof(StringEnumConverter))]
    ReverseSwapFeePayer ReverseSwapFeePayer = ReverseSwapFeePayer.Recipient)
{
    public const long P2trDustLimitSats = 330L;

    public const long DefaultMinBoardingAmountSats = 5000L;
}