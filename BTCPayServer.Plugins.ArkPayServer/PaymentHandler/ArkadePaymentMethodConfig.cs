namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public record ArkadePaymentMethodConfig(string WalletId, bool GeneratedByStore = false, bool AllowSubDustAmounts = false);