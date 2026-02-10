namespace BTCPayServer.Plugins.Arkade.PaymentHandler;

public record ArkadePaymentMethodConfig(string WalletId, bool GeneratedByStore = false, bool AllowSubDustAmounts = false);