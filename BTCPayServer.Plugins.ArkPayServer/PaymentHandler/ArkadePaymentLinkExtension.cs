using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public class ArkadePaymentLinkExtension : IPaymentLinkExtension
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ArkadeLightningLimitsService _limitsService;

    public ArkadePaymentLinkExtension(
        IServiceProvider serviceProvider,
        ArkadeLightningLimitsService limitsService)
    {
        _serviceProvider = serviceProvider;
        _limitsService = limitsService;
    }
    public PaymentMethodId PaymentMethodId { get; } = ArkadePlugin.ArkadePaymentMethodId;

    public string GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper)
    {
        // Get other payment methods if available
        var onchain = prompt.ParentEntity.GetPaymentPrompt(PaymentTypes.CHAIN.GetPaymentMethodId("BTC"));
        var ln = prompt.ParentEntity.GetPaymentPrompt(PaymentTypes.LN.GetPaymentMethodId("BTC"));
        var lnurl = prompt.ParentEntity.GetPaymentPrompt(PaymentTypes.LNURL.GetPaymentMethodId("BTC"));

        var amount = prompt.Calculate().Due;
        
        // Build BIP21 URI using the helper
        var builder = ArkadeBip21Builder.Create()
            .WithArkAddress(prompt.Destination)
            .WithAmount(amount);
        
        // Add onchain address if available, otherwise use boarding address
        if (!string.IsNullOrEmpty(onchain?.Destination))
        {
            builder.WithOnchainAddress(onchain.Destination);
        }
        else if (prompt.Details is not null)
        {
            var handler = _serviceProvider.GetRequiredService<ArkadePaymentMethodHandler>();
            var details = handler.ParsePaymentPromptDetails(prompt.Details);
            if (!string.IsNullOrEmpty(details.BoardingAddress))
            {
                builder.WithOnchainAddress(details.BoardingAddress);
            }
        }
        
        // Add lightning invoice if available and within Boltz limits (prefer LN over LNURL)
        if (ShouldIncludeLightning(prompt).Result)
        {
            if (ln is not null)
            {
                builder.WithLightning(ln.Destination);
            }
            else if (lnurl is not null && _serviceProvider.GetServices<IPaymentLinkExtension>()
                         .FirstOrDefault(p => p.PaymentMethodId == lnurl.PaymentMethodId) is {} lnurlLink)
            {
                if (lnurlLink.GetPaymentLink(lnurl, urlHelper) is { } link)
                {
                    builder.WithLightning(link.Replace("lightning:", String.Empty));
                }
            }
        }
        
        return builder.Build();
    }

    private async Task<bool> ShouldIncludeLightning(PaymentPrompt prompt)
    {
        // Get the invoice amount in satoshis
        var amountSats = (long)Money.Coins(prompt.Calculate().Due).Satoshi;

        // Use the centralized limits service to determine if Lightning should be included
        // This handles caching of store configuration and Boltz limits validation
        return await _limitsService.CanSupportLightningAsync(
            prompt.ParentEntity.StoreId, 
            amountSats, 
            CancellationToken.None);
    }
}
