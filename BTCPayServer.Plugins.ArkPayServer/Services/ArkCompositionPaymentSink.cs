using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Composition;
using NArk.ArkadeIntents.Models;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public sealed class ArkCompositionPaymentSink(InvoiceRepository invoices, PaymentService payments,
    PaymentMethodHandlerDictionary handlers, EventAggregator events, ArkCompositionSourceEvidence sourceEvidence,
    IArkCompositionExecutionLock executionLock, IArkadeIntentStorage intents) : IArkCompositionPaymentSink
{
    public async Task SettleAsync(ArkCompositionRoute route, ComposedSwapExecutionResult result,
        CancellationToken cancellationToken)
    {
        if (route.InvoiceId is null || result.EvmClaimTxid is null || result.DeliveredAmount is null)
            throw new InvalidOperationException("Only verified EVM delivery can create a composed payment.");
        if (result.OutgoingSwapId != route.OutgoingSwapId || result.IngressSwapId != route.IngressSwapId)
            throw new InvalidOperationException("The execution result identifies a different route.");
        // Re-read the SDK intents: they are the durable verified journal. A result that
        // does not match stored, receipt-proven delivery settles nothing.
        var outgoing = await intents.GetArkadeSwapIntent(route.OutgoingSwapId, cancellationToken)
            ?? throw new InvalidOperationException("The outgoing SDK intent is unavailable.");
        if (outgoing.WalletId != route.WalletId || outgoing.PaymentHash != route.PaymentHash ||
            outgoing.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmClaimTxid) != result.EvmClaimTxid)
            throw new InvalidOperationException("The payment does not match the durable verified route.");
        var ingressFeeSats = 0L;
        if (route.IngressSwapId is not null)
        {
            var ingress = await intents.GetArkadeSwapIntent(route.IngressSwapId, cancellationToken)
                ?? throw new InvalidOperationException("The ingress SDK intent is unavailable.");
            ingressFeeSats = checked(ingress.OfferAmount.Satoshi - ingress.WantAmount.Satoshi);
        }
        await using var lease = await executionLock.AcquireAsync($"invoice:{route.StoreId}:{route.InvoiceId}", cancellationToken);
        var invoice = await invoices.GetInvoice(route.InvoiceId);
        if (invoice is null || invoice.StoreId != route.StoreId)
            throw new InvalidOperationException("The composition invoice does not belong to this store.");
        var method = PaymentMethodId.Parse(route.PaymentMethodId);
        var prompt = invoice.GetPaymentPrompt(method);
        if (prompt is null || !handlers.TryGetValue(method, out var handler))
            throw new InvalidOperationException("The composition invoice has no payment handler for its funded rail.");
        var source = route.PaymentMethodId == "BTC-CHAIN"
            ? await sourceEvidence.ReadOrCaptureAsync(route.StoreId, route.RouteId, route.PaymentHash,
                route.CustomerDestination!, checked(route.BaseAmountSats + ingressFeeSats), cancellationToken) : null;
        var items = CreatePayments(route, outgoing.ToAssetId!, result.EvmClaimTxid, result.DeliveredAmount,
            ingressFeeSats, invoice, handler, source);
        foreach (var item in items)
        {
            var existing = invoice.GetPayments(false).SingleOrDefault(p => p.PaymentMethodId == method && p.Id == item.Id);
            if (existing is not null)
            {
                if (existing.Status != PaymentStatus.Settled || existing.Value != item.Amount
                    || existing.Details?["compositionRouteId"]?.Value<string>() != route.RouteId.ToString("N"))
                    throw new InvalidOperationException("An existing payment conflicts with the verified composition.");
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var payment = await payments.AddPayment(item);
            if (payment is not null)
                events.Publish(new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
        }
        events.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
    }

    public static IReadOnlyList<PaymentData> CreatePayments(ArkCompositionRoute route, string assetId,
        string evmClaimTransactionId, string evmDeliveredAmount, long ingressFeeSats, InvoiceEntity invoice,
        IPaymentMethodHandler handler, ArkCompositionSourceFunding? source = null)
    {
        if (route.InvoiceId != invoice.Id || invoice.StoreId != route.StoreId ||
            handler.PaymentMethodId.ToString() != route.PaymentMethodId ||
            string.IsNullOrEmpty(route.CustomerDestination) || invoice.GetPaymentPrompt(handler.PaymentMethodId) is null)
            throw new InvalidOperationException("Payment creation requires the matching verified EVM route and invoice.");
        var amount = checked(route.BaseAmountSats + ingressFeeSats);
        var candidates = new List<(string Id, long Sats, object Details)>();
        switch (route.PaymentMethodId)
        {
            case "ARKADE":
                var id = "evm:" + route.RouteId.ToString("N");
                candidates.Add((id, amount, new ArkadePaymentData(id, route.CustomerDestination)));
                break;
            case "BTC-LN":
                candidates.Add((route.PaymentHash, amount, new LightningLikePaymentData
                {
                    PaymentHash = uint256.Parse(route.PaymentHash)
                }));
                break;
            case "BTC-CHAIN":
                if (source is null || source.StoreId != route.StoreId || source.RouteId != route.RouteId
                    || source.PaymentHash != route.PaymentHash || source.Destination != route.CustomerDestination
                    || source.Outputs.Length == 0 || source.Outputs.Sum(o => o.AmountSats) != amount)
                    throw new InvalidOperationException("Verified onchain source outputs are required for this payment rail.");
                foreach (var output in source.Outputs)
                {
                    var point = new OutPoint(uint256.Parse(output.TransactionId), output.OutputIndex);
                    candidates.Add((point.ToString(), output.AmountSats,
                        new BitcoinLikePaymentData(point, false, null!, 0)));
                }
                break;
            default: throw new InvalidOperationException("Unsupported composed payment rail.");
        }
        return candidates.Select(candidate =>
        {
            var details = JObject.FromObject(candidate.Details, handler.Serializer);
            details.Remove("preimage");
            details.Remove("Preimage");
            details["compositionRouteId"] = route.RouteId.ToString("N");
            details["evmClaimTransactionId"] = evmClaimTransactionId;
            details["evmAssetId"] = assetId;
            details["evmDeliveredAmount"] = evmDeliveredAmount;
            details["destination"] = route.CustomerDestination;
            var payment = new PaymentData
            {
                Id = candidate.Id, Currency = "BTC", Status = PaymentStatus.Settled,
                Amount = Money.Satoshis(candidate.Sats).ToDecimal(MoneyUnit.BTC), Created = DateTimeOffset.UtcNow
            }.Set(invoice, handler, details);
            var blob = JObject.Parse(payment.Blob2);
            blob["destination"] = route.CustomerDestination;
            payment.Blob2 = blob.ToString(Formatting.None);
            return payment;
        }).ToArray();
    }
}
