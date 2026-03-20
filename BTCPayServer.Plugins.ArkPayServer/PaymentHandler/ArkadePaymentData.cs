namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public record ArkadePaymentData(string Outpoint, string? Destination = null);