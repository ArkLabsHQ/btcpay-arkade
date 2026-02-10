using BTCPayServer.Data;
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.Arkade.PaymentHandler;

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
        context.Model.InvoiceBitcoinUrlQR = 
            paymentLink
                .ToUpperInvariant()
                .Replace("BITCOIN:","bitcoin:")
                .Replace("LIGHTNING=","lightning=")
                .Replace("ARK=","ark=");
        context.Model.InvoiceBitcoinUrl = paymentLink;
        
        if (context.Store.GetStoreBlob().OnChainWithLnInvoiceFallback)
        {
            var ln = PaymentTypes.LN.GetPaymentMethodId("BTC");
            var lnurl = PaymentTypes.LNURL.GetPaymentMethodId("BTC");
            var onchain = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
            var pmis = new List<PaymentMethodId> { ln, lnurl, onchain };
            context.Model.AvailablePaymentMethods.Where(method => pmis.Contains(method.PaymentMethodId)).ToList().ForEach(method => method.Displayed = false);
        }
        //
        // context.Model.InvoiceBitcoinUrl = _paymentLinkExtension.GetPaymentLink(context.Prompt, context.UrlHelper);
        // context.Model.InvoiceBitcoinUrlQR = context.Model.InvoiceBitcoinUrl;
        // context.Model.ShowPayInWalletButton = false;
        // context.Model.PaymentMethodCurrency = configurationItem.CurrencyDisplayName;

    }
}