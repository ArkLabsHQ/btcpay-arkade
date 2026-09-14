using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public static class ArkCompositionOnchainRegistration
{
    public static IServiceCollection AddArkCompositionOnchainPrompts(this IServiceCollection services)
    {
        services.AddSingleton<ArkCompositionCheckoutPolicy>();
        services.Replace(ServiceDescriptor.Singleton<PaymentMethodHandlerDictionary>(provider =>
            new PaymentMethodHandlerDictionary(provider.GetServices<IPaymentMethodHandler>().Select(handler =>
                handler is BitcoinLikePaymentHandler bitcoin && handler.PaymentMethodId.ToString() == "BTC-CHAIN"
                    ? ActivatorUtilities.CreateInstance<ArkCompositionOnchainPaymentHandler>(provider, bitcoin,
                        provider.GetRequiredService<ArkCompositionPromptService>())
                    : handler is LightningLikePaymentHandler lightning && handler.PaymentMethodId.ToString() == "BTC-LN"
                        ? ActivatorUtilities.CreateInstance<ArkCompositionLightningPaymentHandler>(provider, lightning,
                            provider.GetRequiredService<ArkCompositionPromptService>())
                    : handler))));
        return services;
    }
}
