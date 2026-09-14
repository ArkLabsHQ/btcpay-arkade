using BTCPayServer.HostedServices;
using BTCPayServer.Client.Models;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services;
using BTCPayServer.Services.Fees;
using BTCPayServer.Services.Wallets;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public sealed class ArkCompositionOnchainPaymentHandler : BitcoinLikePaymentHandler, IPaymentMethodHandler
{
    private readonly IPaymentMethodHandler _inner;
    private readonly ArkCompositionPromptService _prompts;

    public ArkCompositionOnchainPaymentHandler(BitcoinLikePaymentHandler inner, ArkCompositionPromptService prompts,
        ExplorerClientProvider explorerProvider, IFeeProviderFactory feeProviderFactory, DisplayFormatter displayFormatter,
        NBXplorerDashboard dashboard, WalletRepository walletRepository, BTCPayWalletProvider walletProvider)
        : base(inner.PaymentMethodId, explorerProvider, inner.Network, feeProviderFactory, displayFormatter, dashboard,
            walletRepository, walletProvider)
    {
        _inner = inner;
        _prompts = prompts;
    }

    Task IPaymentMethodHandler.BeforeFetchingRates(PaymentMethodContext context)
    {
        if (!ArkCompositionPromptService.Enabled(context.Store, "BTC-CHAIN"))
            return _inner.BeforeFetchingRates(context);
        context.Prompt.Currency = Network.CryptoCode;
        context.Prompt.Divisibility = Network.Divisibility;
        context.Prompt.PaymentMethodFee = 0m;
        return Task.CompletedTask;
    }

    async Task IPaymentMethodHandler.ConfigurePrompt(PaymentMethodContext context)
    {
        if (!ArkCompositionPromptService.Enabled(context.Store, "BTC-CHAIN"))
        {
            await _inner.ConfigurePrompt(context);
            return;
        }

        var route = await _prompts.TryCreateAsync(context.Store, context.InvoiceEntity, "BTC-CHAIN",
            Money.Coins(context.Prompt.Calculate().Due).Satoshi, context.InvoiceEntity.ExpirationTime)
            ?? throw new PaymentMethodUnavailableException("The onchain composition policy is unavailable.");
        context.Prompt.Destination = route.CustomerDestination!;
        context.Prompt.PaymentMethodFee = Money.Satoshis(route.IngressFeeSats).ToDecimal(MoneyUnit.BTC);
        context.Prompt.Details = JObject.FromObject(new ArkCompositionOnchainPromptDetails
        {
            CompositionRouteId = route.RouteId, PaymentHash = route.PaymentHash,
            CheckoutExpiresAt = route.CheckoutExpiresAt, FeeMode = NetworkFeeMode.Never,
            PaymentMethodFeeRate = FeeRate.Zero, RecommendedFeeRate = FeeRate.Zero, PayjoinEnabled = false
        }, Serializer);
    }

    Task IPaymentMethodHandler.AfterSavingInvoice(PaymentMethodContext context) =>
        context.Prompt.Details?["compositionRouteId"] is not null || ArkCompositionPromptService.Enabled(context.Store, "BTC-CHAIN")
            ? Task.CompletedTask : _inner.AfterSavingInvoice(context);

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details) =>
        details["compositionRouteId"] is not null
            ? details.ToObject<ArkCompositionOnchainPromptDetails>(Serializer)!
            : _inner.ParsePaymentPromptDetails(details);

    object IPaymentMethodHandler.ParsePaymentMethodConfig(JToken config) => _inner.ParsePaymentMethodConfig(config);
    object IPaymentMethodHandler.ParsePaymentDetails(JToken details) => _inner.ParsePaymentDetails(details);
    void IPaymentMethodHandler.StripDetailsForNonOwner(object details) => _inner.StripDetailsForNonOwner(details);
    Task IPaymentMethodHandler.ValidatePaymentMethodConfig(PaymentMethodConfigValidationContext context) =>
        _inner.ValidatePaymentMethodConfig(context);
}

public sealed class ArkCompositionOnchainPromptDetails : BitcoinPaymentPromptDetails
{
    public Guid CompositionRouteId { get; init; }
    public string? PaymentHash { get; init; }
    public long? CheckoutExpiresAt { get; init; }
}
