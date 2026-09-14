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
    private readonly ArkadeLightningAvailabilityService _availability;
    private readonly ArkCompositionCheckoutPolicy? _compositionPolicy;

    public ArkadePaymentLinkExtension(
        IServiceProvider serviceProvider,
        ArkadeLightningAvailabilityService availability,
        ArkCompositionCheckoutPolicy? compositionPolicy = null)
    {
        _serviceProvider = serviceProvider;
        _availability = availability;
        _compositionPolicy = compositionPolicy;
    }
    public PaymentMethodId PaymentMethodId { get; } = ArkadePlugin.ArkadePaymentMethodId;

    public string GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper)
    {
        // Get other payment methods if available
        var onchain = prompt.ParentEntity.GetPaymentPrompt(PaymentTypes.CHAIN.GetPaymentMethodId("BTC"));
        var ln = prompt.ParentEntity.GetPaymentPrompt(PaymentTypes.LN.GetPaymentMethodId("BTC"));
        var lnurl = prompt.ParentEntity.GetPaymentPrompt(PaymentTypes.LNURL.GetPaymentMethodId("BTC"));

        var amount = prompt.Calculate().Due;
        var composed = _compositionPolicy?.RequiresComposition(prompt.ParentEntity) ??
            prompt.Details?["compositionRouteId"] is not null;
        if (composed)
        {
            if (_compositionPolicy?.CanInclude(onchain, true) != true || onchain!.Calculate().Due != amount)
                onchain = null;
            if (_compositionPolicy?.CanInclude(ln, true) != true) ln = null;
            lnurl = null;
        }

        // Build BIP21 URI using the helper
        var builder = ArkadeBip21Builder.Create()
            .WithArkAddress(prompt.Destination)
            .WithAmount(amount);

        // Add onchain address if available, otherwise use boarding address.
        // When an onchain payment method is present, also delegate to its own
        // IPaymentLinkExtension so any params other plugins attached to the
        // upstream BIP21 (PayJoin's `pj=`, Branta's `branta_*`, etc.) carry
        // through to the unified Arkade QR. Without this the Arkade tab
        // clobbers those params (issue: Branta + PayJoin lose their hooks).
        if (!string.IsNullOrEmpty(onchain?.Destination))
        {
            builder.WithOnchainAddress(onchain.Destination);

            var onchainLink = _serviceProvider.GetServices<IPaymentLinkExtension>()
                .FirstOrDefault(p => p.PaymentMethodId == onchain.PaymentMethodId);
            if (onchainLink is not null)
            {
                var upstream = onchainLink.GetPaymentLink(onchain, urlHelper);
                var qIdx = upstream?.IndexOf('?') ?? -1;
                if (qIdx >= 0)
                    builder.WithExtraQuery(upstream![(qIdx + 1)..], composed);
            }
        }
        else if (!composed && prompt.Details is not null)
        {
            var handler = _serviceProvider.GetRequiredService<ArkadePaymentMethodHandler>();
            var details = handler.ParsePaymentPromptDetails(prompt.Details);
            if (!string.IsNullOrEmpty(details.BoardingAddress))
            {
                builder.WithOnchainAddress(details.BoardingAddress);
            }
        }
        
        // Add lightning invoice if available and within Boltz limits (prefer LN over LNURL)
        if (composed ? ln is not null : ShouldIncludeLightning(prompt).GetAwaiter().GetResult())
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
        var amountSats = (long)Money.Coins(prompt.Calculate().Due).Satoshi;

        return await _availability.ShouldOfferLightningAsync(
            prompt.ParentEntity.StoreId,
            amountSats,
            CancellationToken.None);
    }
}
