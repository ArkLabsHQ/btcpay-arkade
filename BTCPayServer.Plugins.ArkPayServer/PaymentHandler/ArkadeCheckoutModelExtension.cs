using System.Text.RegularExpressions;
using BTCPayServer.Data;
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public class ArkadeCheckoutModelExtension: ICheckoutModelExtension
{
    private readonly IPaymentLinkExtension _arkadePaymentLinkExtension;

    public ArkadeCheckoutModelExtension(IEnumerable<IPaymentLinkExtension> paymentLinkExtensions)
    {
        _arkadePaymentLinkExtension =
            paymentLinkExtensions
                .SingleOrDefault(p => p.PaymentMethodId == ArkadePlugin.ArkadePaymentMethodId) ??
            throw new InvalidOperationException("ArkadePaymentLinkExtension not found in DI");
    }
    public PaymentMethodId PaymentMethodId => ArkadePlugin.ArkadePaymentMethodId;

    public string Image => "arkade.svg";

    public string Badge => "";//"👾";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context is not { Handler: ArkadePaymentMethodHandler handler })
            return;

        context.Model.CheckoutBodyComponentName = ArkadePlugin.CheckoutBodyComponentName;
        context.Model.ShowRecommendedFee = false;
        var paymentLink =
            _arkadePaymentLinkExtension.GetPaymentLink(context.Prompt, context.UrlHelper)
                ?? throw new Exception("Failed to generate Arkade payment link"); // should not happen

        // QR code: strip lightning= parameter to keep the QR small and scannable.
        // The bolt11 is shown as a separate text field in the checkout component.
        var qrLink = StripLightningParam(paymentLink);
        context.Model.InvoiceBitcoinUrlQR =
            qrLink
                .ToUpperInvariant()
                .Replace("BITCOIN:","bitcoin:")
                .Replace("ARK=","ark=");
        // Full BIP21 with all params for "Pay in wallet" link
        context.Model.InvoiceBitcoinUrl = paymentLink;
    }

    /// <summary>
    /// Removes the lightning= parameter from a BIP21 URI while preserving other params.
    /// </summary>
    private static string StripLightningParam(string bip21Uri)
    {
        // Remove &lightning=... or ?lightning=...& patterns
        var result = Regex.Replace(bip21Uri, @"[&?]lightning=[^&]*", "", RegexOptions.IgnoreCase);
        // Fix dangling ? if lightning was the first param
        if (result.Contains('?') && result.EndsWith('?'))
            result = result.TrimEnd('?');
        // Fix case where lightning was first param and others follow: "bitcoin:addr?&ark=..." -> "bitcoin:addr?ark=..."
        result = result.Replace("?&", "?");
        return result;
    }
}