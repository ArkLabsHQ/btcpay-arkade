using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>
/// Payment link for the Arkade Asset method: the BIP-321 URI of the first
/// offered asset option. The checkout component lets the payer switch between
/// options client-side, so the link is just a sensible default (and what
/// non-checkout consumers, e.g. the invoice API, surface).
/// </summary>
public class ArkadeAssetPaymentLinkExtension(ArkadeAssetPaymentMethodHandler handler) : IPaymentLinkExtension
{
    public PaymentMethodId PaymentMethodId { get; } = ArkadePlugin.ArkadeAssetPaymentMethodId;

    public string? GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper)
    {
        if (prompt.Details is null)
            return null;
        var details = handler.ParsePaymentPromptDetails(prompt.Details);
        return details.Options.FirstOrDefault()?.Bip321Uri;
    }
}
