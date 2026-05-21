using BTCPayServer.Data;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public class ArkadeCheckoutModelExtension: ICheckoutModelExtension, IGlobalCheckoutModelExtension
{
    private readonly IPaymentLinkExtension _arkadePaymentLinkExtension;
    private readonly IPaymentLinkExtension? _bitcoinPaymentLinkExtension;
    private readonly ArkadePaymentMethodHandler _handler;
    private readonly PaymentMethodHandlerDictionary _handlers;
    // IServiceProvider, not IEnumerable<IGlobalCheckoutModelExtension>: this
    // type is itself registered as IGlobalCheckoutModelExtension, so injecting
    // the enumerable directly creates a DI cycle (the resolver builds every
    // implementation before our constructor returns, including ourselves).
    // Lazily resolving through the provider sidesteps the cycle since the
    // singleton is cached by the time we actually enumerate at request time.
    private readonly IServiceProvider _serviceProvider;

    private static readonly PaymentMethodId BitcoinOnchainPmi =
        PaymentTypes.CHAIN.GetPaymentMethodId("BTC");

    public ArkadeCheckoutModelExtension(
        IEnumerable<IPaymentLinkExtension> paymentLinkExtensions,
        IServiceProvider serviceProvider,
        PaymentMethodHandlerDictionary handlers,
        ArkadePaymentMethodHandler handler)
    {
        _handler = handler;
        _handlers = handlers;
        _serviceProvider = serviceProvider;
        var linkList = paymentLinkExtensions as IList<IPaymentLinkExtension> ?? paymentLinkExtensions.ToList();
        _arkadePaymentLinkExtension =
            linkList.SingleOrDefault(p => p.PaymentMethodId == ArkadePlugin.ArkadePaymentMethodId) ??
            throw new InvalidOperationException("ArkadePaymentLinkExtension not found in DI");
        _bitcoinPaymentLinkExtension =
            linkList.SingleOrDefault(p => p.PaymentMethodId == BitcoinOnchainPmi);
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

        // Harvest any params that bitcoin-onchain-only plugins (Branta's
        // branta_id/branta_secret, payjoin's pj=, future ones that gate on
        // PaymentMethodId == "BTC-CHAIN") would have appended to the bitcoin
        // tab's URL. They never see the Arkade tab on their own — we have to
        // synthesise their pipeline. See HarvestUpstreamGlobalParams below.
        var (extraForUrl, extraForQr) = HarvestUpstreamGlobalParams(context);

        // Full BIP21 with all params for "Pay in wallet" link
        context.Model.InvoiceBitcoinUrl = AppendQuery(paymentLink, extraForUrl);

        // QR code: uppercase address + bech32m values for efficient alphanumeric QR encoding,
        // keeping parameter keys lowercase per BIP21 spec. Includes lightning= for unified QR.
        // Other plugin-contributed params are forwarded verbatim — uppercasing would mangle
        // case-sensitive payloads (PayJoin's onion URLs, Branta's base64 secrets, etc.).
        context.Model.InvoiceBitcoinUrlQR = AppendQuery(UpperCaseQrUri(paymentLink), extraForQr);

        // Pass boarding flag to checkout component
        if (context.Prompt.Details is not null)
        {
            var details = _handler.ParsePaymentPromptDetails(context.Prompt.Details);
            if (!string.IsNullOrEmpty(details.BoardingAddress))
                context.Model.AdditionalData["hasBoardingAddress"] = JToken.FromObject(true);
        }
    }

    /// <summary>
    /// Returns (extraForUrl, extraForQr): the <c>key=value</c> entries that
    /// other plugins' <see cref="IGlobalCheckoutModelExtension"/> hooks would
    /// have added to the bitcoin onchain tab's URL had the user been viewing
    /// that tab. We synthesise a bitcoin-tab CheckoutModel + Context, run the
    /// rest of the global-extension pipeline against it (skipping ourselves
    /// to avoid recursion), and diff the resulting URL strings against the
    /// pristine bitcoin BIP21 to identify what each plugin tacked on.
    /// </summary>
    /// <remarks>
    /// <para>This is a pragmatic workaround for plugins that gate on
    /// <c>model.PaymentMethodId == "BTC-CHAIN"</c> (notably Branta) and
    /// therefore never contribute to the Arkade tab. The right long-term fix
    /// is for those plugins to also recognise the Arkade payment method;
    /// until then we replay their work on a synthetic context to harvest
    /// what they would have added.</para>
    /// <para>The trade-off: any plugin doing more than URL mutation in its
    /// global hook (DB writes, metrics, cache reads/writes) will see those
    /// side-effects fire a second time per checkout-page render. Branta's
    /// hook is pure URL concatenation, so today this is safe. We're
    /// deliberately accepting the brittleness for the UX win.</para>
    /// </remarks>
    private (string ExtraForUrl, string ExtraForQr) HarvestUpstreamGlobalParams(CheckoutModelContext realCtx)
    {
        var bitcoinPrompt = realCtx.InvoiceEntity.GetPaymentPrompt(BitcoinOnchainPmi);
        if (bitcoinPrompt is null || _bitcoinPaymentLinkExtension is null) return ("", "");
        var bitcoinHandler = _handlers.TryGet(BitcoinOnchainPmi);
        if (bitcoinHandler is null) return ("", "");

        var pristineBitcoinUrl = _bitcoinPaymentLinkExtension.GetPaymentLink(bitcoinPrompt, realCtx.UrlHelper);
        if (string.IsNullOrEmpty(pristineBitcoinUrl)) return ("", "");

        // Synthetic model with PaymentMethodId == "BTC-CHAIN" so plugins that
        // gate on that check (Branta) treat this as the bitcoin tab.
        var synthetic = new CheckoutModel
        {
            PaymentMethodId = BitcoinOnchainPmi.ToString(),
            PaymentMethodCurrency = realCtx.Model.PaymentMethodCurrency,
            InvoiceBitcoinUrl = pristineBitcoinUrl,
            InvoiceBitcoinUrlQR = pristineBitcoinUrl,
        };
        var syntheticCtx = realCtx with
        {
            Model = synthetic,
            Prompt = bitcoinPrompt,
            Handler = bitcoinHandler,
        };

        foreach (var global in _serviceProvider.GetServices<IGlobalCheckoutModelExtension>())
        {
            if (ReferenceEquals(global, this)) continue; // avoid recursion
            try
            {
                global.ModifyCheckoutModel(syntheticCtx);
            }
            catch
            {
                // Best-effort. A misbehaving global shouldn't break the Arkade tab.
            }
        }

        return (
            DiffAddedQuery(pristineBitcoinUrl, synthetic.InvoiceBitcoinUrl),
            DiffAddedQuery(pristineBitcoinUrl, synthetic.InvoiceBitcoinUrlQR));
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
    /// Uppercases only the address portion of the BIP21 URI for QR
    /// alphanumeric-mode efficiency, mirroring what BTCPay itself does for
    /// the bitcoin tab in <c>BitcoinCheckoutModelExtension</c>. Param values
    /// are passed through verbatim — we have no protocol-agnostic way to
    /// know whether a given value's payload is case-insensitive (bech32m,
    /// decimal) or case-sensitive (URLs, base64, JWTs, future plugins'
    /// data), so the safe default is to leave them alone.
    /// </summary>
    private static string UpperCaseQrUri(string bip21Uri)
    {
        var qIdx = bip21Uri.IndexOf('?');
        if (qIdx < 0)
            return "bitcoin:" + bip21Uri["bitcoin:".Length..].ToUpperInvariant();

        var address = bip21Uri["bitcoin:".Length..qIdx].ToUpperInvariant();
        var queryPart = bip21Uri[qIdx..]; // includes leading '?'
        return $"bitcoin:{address}{queryPart}";
    }

    /// <summary>
    /// Returns the entries from <paramref name="after"/>'s query string whose
    /// keys are absent from <paramref name="before"/>'s query string. Values
    /// are returned verbatim (no decode/re-encode) so whatever URL-encoding
    /// the contributing plugin chose round-trips byte-for-byte into our
    /// final URL.
    /// </summary>
    private static string DiffAddedQuery(string before, string after)
    {
        if (string.IsNullOrEmpty(after) || ReferenceEquals(before, after)) return "";

        var beforeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in EnumerateQueryEntries(before))
        {
            var eq = entry.IndexOf('=');
            beforeKeys.Add(eq < 0 ? entry : entry[..eq]);
        }

        var added = new List<string>();
        foreach (var entry in EnumerateQueryEntries(after))
        {
            var eq = entry.IndexOf('=');
            var key = eq < 0 ? entry : entry[..eq];
            if (!beforeKeys.Contains(key))
                added.Add(entry);
        }
        return string.Join("&", added);
    }

    private static IEnumerable<string> EnumerateQueryEntries(string url)
    {
        if (string.IsNullOrEmpty(url)) return [];
        var qIdx = url.IndexOf('?');
        if (qIdx < 0) return [];
        return url[(qIdx + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string AppendQuery(string url, string entries)
    {
        if (string.IsNullOrEmpty(entries)) return url;
        var sep = url.Contains('?') ? "&" : "?";
        return url + sep + entries;
    }
}