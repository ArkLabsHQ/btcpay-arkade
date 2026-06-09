using BTCPayServer.Payments;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>
/// Checkout model for the Arkade Asset method: selects the multi-asset picker
/// component and hands it one option per enabled asset (ticker + amount due +
/// BIP-321 URI) so the payer chooses which asset to send. All options settle to
/// the same Ark address (the prompt's Destination, surfaced as <c>model.address</c>).
/// </summary>
public class ArkadeAssetCheckoutModelExtension(ArkadeAssetPaymentMethodHandler handler) : ICheckoutModelExtension
{
    public PaymentMethodId PaymentMethodId => ArkadePlugin.ArkadeAssetPaymentMethodId;

    public string Image => "arkade.svg";

    public string Badge => "";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context is not { Handler: ArkadeAssetPaymentMethodHandler })
            return;

        context.Model.CheckoutBodyComponentName = ArkadePlugin.AssetCheckoutBodyComponentName;
        context.Model.ShowRecommendedFee = false;

        if (context.Prompt.Details is null)
            return;

        var details = handler.ParsePaymentPromptDetails(context.Prompt.Details);
        context.Model.AdditionalData["arkadeAssetOptions"] = JToken.FromObject(
            details.Options.Select(o => new
            {
                assetId = o.AssetId,
                currencyCode = o.CurrencyCode,
                ticker = o.Ticker,
                decimals = o.Decimals,
                formattedDue = o.FormattedDue,
                bip321Uri = o.Bip321Uri,
            }).ToList());
    }
}
