namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <param name="SolverFeeSats">What the solver kept on a swap; see <see cref="OnchainSwapInvoicePolicy.SolverFee"/>.</param>
public record ArkadePaymentData(
    string Outpoint, string? Destination = null, bool IsBoarding = false, long? SolverFeeSats = null);