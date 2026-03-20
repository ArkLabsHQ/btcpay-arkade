using BTCPayServer.Data;
using BTCPayServer.Payments;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public class ArkadeCheckoutModelExtension: ICheckoutModelExtension, IGlobalCheckoutModelExtension
{
    private readonly IPaymentLinkExtension _arkadePaymentLinkExtension;
    private readonly ArkadePaymentMethodHandler _handler;

    public ArkadeCheckoutModelExtension(
        IEnumerable<IPaymentLinkExtension> paymentLinkExtensions,
        ArkadePaymentMethodHandler handler)
    {
        _handler = handler;
        _arkadePaymentLinkExtension =
            paymentLinkExtensions
                .SingleOrDefault(p => p.PaymentMethodId == ArkadePlugin.ArkadePaymentMethodId) ??
            throw new InvalidOperationException("ArkadePaymentLinkExtension not found in DI");
    }
    public PaymentMethodId PaymentMethodId => ArkadePlugin.ArkadePaymentMethodId;

    public string Image => "arkade.svg";

    public string Badge => "";//"👾";

    void ICheckoutModelExtension.ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context is not { Handler: ArkadePaymentMethodHandler })
            return;

        context.Model.CheckoutBodyComponentName = ArkadePlugin.CheckoutBodyComponentName;
        context.Model.ShowRecommendedFee = false;
        var paymentLink =
            _arkadePaymentLinkExtension.GetPaymentLink(context.Prompt, context.UrlHelper)
                ?? throw new Exception("Failed to generate Arkade payment link"); // should not happen

        // QR code: uppercase address and values for efficient alphanumeric QR encoding,
        // keeping parameter keys lowercase per BIP21 spec. Includes lightning= for unified QR.
        context.Model.InvoiceBitcoinUrlQR = UpperCaseQrUri(paymentLink);
        // Full BIP21 with all params for "Pay in wallet" link
        context.Model.InvoiceBitcoinUrl = paymentLink;

        // Pass boarding flag to checkout component
        if (context.Prompt.Details is not null)
        {
            var details = _handler.ParsePaymentPromptDetails(context.Prompt.Details);
            if (!string.IsNullOrEmpty(details.BoardingAddress))
                context.Model.AdditionalData["hasBoardingAddress"] = JToken.FromObject(true);
        }
    }

    void IGlobalCheckoutModelExtension.ModifyCheckoutModel(CheckoutModelContext context)
    {
        // Hide LN/LNURL tabs when Arkade is displayed and OnChainWithLnInvoiceFallback is enabled,
        // since the Arkade BIP21 already embeds the lightning= parameter.
        if (context.StoreBlob is not { OnChainWithLnInvoiceFallback: true })
            return;

        var hasArkade = context.Model.AvailablePaymentMethods
            .Any(pm => pm.PaymentMethodId == ArkadePlugin.ArkadePaymentMethodId && pm.Displayed);
        if (!hasArkade)
            return;

        var lnId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var lnurlId = PaymentTypes.LNURL.GetPaymentMethodId("BTC");
        foreach (var pm in context.Model.AvailablePaymentMethods)
        {
            if (pm.PaymentMethodId == lnId || pm.PaymentMethodId == lnurlId)
                pm.Displayed = false;
        }
    }

    /// <summary>
    /// Uppercases address and parameter values for efficient QR alphanumeric encoding,
    /// while keeping the scheme and parameter keys lowercase per BIP21 spec.
    /// </summary>
    private static string UpperCaseQrUri(string bip21Uri)
    {
        // Split into base (bitcoin:address) and query (?params)
        var qIdx = bip21Uri.IndexOf('?');
        if (qIdx < 0)
            return "bitcoin:" + bip21Uri["bitcoin:".Length..].ToUpperInvariant();

        var basePart = bip21Uri[..qIdx];
        var queryPart = bip21Uri[(qIdx + 1)..];

        // Uppercase the address
        var address = basePart["bitcoin:".Length..].ToUpperInvariant();

        // Uppercase parameter values but keep keys lowercase
        var parameters = queryPart.Split('&')
            .Select(p =>
            {
                var eqIdx = p.IndexOf('=');
                if (eqIdx < 0) return p;
                var key = p[..eqIdx].ToLowerInvariant();
                var value = p[(eqIdx + 1)..].ToUpperInvariant();
                return $"{key}={value}";
            });

        return $"bitcoin:{address}?{string.Join("&", parameters)}";
    }
}