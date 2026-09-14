using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public sealed class ArkCompositionLightningPaymentHandler : LightningLikePaymentHandler, IPaymentMethodHandler
{
    private readonly LightningLikePaymentHandler _inner;
    private readonly ArkCompositionPromptService _prompts;

    public ArkCompositionLightningPaymentHandler(LightningLikePaymentHandler inner, ArkCompositionPromptService prompts,
        NBXplorerDashboard dashboard, LightningClientFactoryService lightningClientFactory, SocketFactory socketFactory,
        ISettingsAccessor<PoliciesSettings> policies)
        : base(inner.PaymentMethodId, dashboard, lightningClientFactory, inner.Network, socketFactory, inner.Options,
            policies, inner.Options)
    {
        _inner = inner;
        _prompts = prompts;
    }

    Task IPaymentMethodHandler.BeforeFetchingRates(PaymentMethodContext context) => ((IPaymentMethodHandler)_inner).BeforeFetchingRates(context);
    Task IPaymentMethodHandler.ConfigurePrompt(PaymentMethodContext context) => ((IPaymentMethodHandler)_inner).ConfigurePrompt(context);

    async Task IPaymentMethodHandler.AfterSavingInvoice(PaymentMethodContext context)
    {
        await ((IPaymentMethodHandler)_inner).AfterSavingInvoice(context);
        var details = context.Prompt.Details;
        await _prompts.AttachLightningAsync(context.Store, context.InvoiceEntity,
            Value(details, "InvoiceId"), Value(details, "PaymentHash"), context.Prompt.Destination, CancellationToken.None);
    }

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details) => _inner.ParsePaymentPromptDetails(details);
    object IPaymentMethodHandler.ParsePaymentMethodConfig(JToken config) => _inner.ParsePaymentMethodConfig(config);
    object IPaymentMethodHandler.ParsePaymentDetails(JToken details) => _inner.ParsePaymentDetails(details);
    void IPaymentMethodHandler.StripDetailsForNonOwner(object details) => ((IPaymentMethodHandler)_inner).StripDetailsForNonOwner(details);
    Task IPaymentMethodHandler.ValidatePaymentMethodConfig(PaymentMethodConfigValidationContext context) =>
        _inner.ValidatePaymentMethodConfig(context);

    private static string? Value(JToken? details, string name) => details is JObject value &&
        value.Properties().SingleOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            is { Value.Type: JTokenType.String } property ? property.Value.Value<string>() : null;
}
