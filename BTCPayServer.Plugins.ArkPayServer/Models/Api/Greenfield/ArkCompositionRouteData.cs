using System.Globalization;
using BTCPayServer.Plugins.ArkPayServer.Data;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;

namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>Explicit public route projection, excluding wallet capabilities and SDK secrets.</summary>
/// <param name="RouteId">Independent prompt or renewal identity.</param>
/// <param name="StoreId">Owning store.</param>
/// <param name="InvoiceId">Exact attached invoice, or null before prompt persistence.</param>
/// <param name="PaymentMethodId">Source rail.</param>
/// <param name="PaymentHash">Public route hash H.</param>
/// <param name="AssetId">CAIP-19 destination asset.</param>
/// <param name="Destination">Merchant EVM address.</param>
/// <param name="CreatedAt">UTC creation time.</param>
/// <param name="Status">Observed-money state; only EvmClaimVerified means settlement.</param>
/// <param name="BaseAmountSats">Exact BTC amount required by L.</param>
/// <param name="IngressFeeSats">Customer ingress spread.</param>
/// <param name="FailureCode">Closed nonsecret recovery code.</param>
/// <param name="Legs">Prepared RFQs and accepted public quotes.</param>
/// <param name="EvmTerms">Exact six-value tuple and allowed contract.</param>
/// <param name="IngressClaimTransactionId">M-to-L transaction.</param>
/// <param name="EvmLockProof">SDK-verified public proof coordinates.</param>
/// <param name="EvmClaimTransactionId">Transaction proving token delivery.</param>
public sealed record ArkCompositionRouteData(Guid RouteId, string StoreId, string? InvoiceId, string PaymentMethodId,
    string PaymentHash, string AssetId, string Destination, DateTimeOffset CreatedAt, string Status,
    long BaseAmountSats, long IngressFeeSats, string? FailureCode, ArkCompositionLegData[] Legs,
    ArkCompositionEvmTerms? EvmTerms, string? IngressClaimTransactionId, ArkCompositionEvmLockProof? EvmLockProof,
    string? EvmClaimTransactionId, string? EvmDeliveredAmount, string? CustomerDestination, long? CheckoutExpiresAt,
    bool ExecutionAvailable)
{
    /// <summary>Ingress payment never constitutes completion.</summary>
    public bool SettlementVerified => Status == "EvmClaimVerified";
    /// <summary>Fixed merchant-delivery completion policy.</summary>
    public string PaymentCompletionCondition => "evm-settlement";

    /// <summary>Projects the index row plus live SDK intent state. No journal is read.</summary>
    public static ArkCompositionRouteData From(ArkCompositionRoute route, ArkadeSwapIntent? outgoing,
        ArkadeSwapIntent? ingress, bool executionAvailable)
    {
        var claimTxid = outgoing?.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmClaimTxid);
        var status = claimTxid is not null ? "EvmClaimVerified" : outgoing?.Status.ToString() ?? "Pending";
        var fee = ingress is null ? 0 : checked(ingress.OfferAmount.Satoshi - ingress.WantAmount.Satoshi);
        ArkCompositionEvmTerms? terms = null;
        string? assetId = "", destination = "";
        if (outgoing?.Type == ArkadeSwapIntentType.BtcToEvm)
        {
            var evm = outgoing.EvmMetadata();
            assetId = outgoing.ToAssetId ?? "";
            destination = evm.ClaimAddress;
            terms = new ArkCompositionEvmTerms(route.PaymentHash, evm.Amount, evm.TokenAddress,
                evm.ClaimAddress, evm.RefundAddress, evm.TimeoutBlock, evm.SwapContractAddress);
        }
        var legs = new List<ArkCompositionLegData>();
        if (outgoing is not null) legs.Add(Leg(outgoing, "Outgoing", outgoing.Type == ArkadeSwapIntentType.BtcToEvm
            ? outgoing.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmAmount) : null));
        if (ingress is not null) legs.Add(Leg(ingress, "Ingress",
            ingress.WantAmount.Satoshi.ToString(CultureInfo.InvariantCulture),
            ingress.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.ComposedPayoutPkScript)));
        return new ArkCompositionRouteData(route.RouteId, route.StoreId, route.InvoiceId, route.PaymentMethodId,
            route.PaymentHash, assetId, destination, route.CreatedAt, status, route.BaseAmountSats, fee,
            outgoing?.Status == ArkadeSwapIntentStatus.Cancelled ? ArkCompositionFailure.RefundRequired.ToString() : null,
            legs.OrderBy(l => l.Kind).ToArray(), terms, ingress?.SpentTxid,
            LockProof(outgoing), claimTxid,
            outgoing?.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmDeliveredAmount),
            route.CustomerDestination, route.CheckoutExpiresAt, executionAvailable);
    }

    private static ArkCompositionLegData Leg(ArkadeSwapIntent intent, string kind, string? toAmount,
        string? payoutScript = null) => new(intent.Id, kind,
        new ArkCompositionQuote(intent.Id, intent.PaymentHash ?? "", intent.SolverPubkey() ?? "",
            intent.OfferAmount.Satoshi.ToString(CultureInfo.InvariantCulture),
            toAmount ?? intent.WantAmount.Satoshi.ToString(CultureInfo.InvariantCulture),
            intent.SwapPkScript, intent.SwapAddress, null, intent.RefundLocktime, payoutScript), null, null);

    private static ArkCompositionEvmLockProof? LockProof(ArkadeSwapIntent? outgoing)
    {
        var observed = outgoing?.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmLockObservedAtBlock);
        var proven = outgoing?.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmLockProvenAtBlock);
        var timestamp = outgoing?.Metadata.GetValueOrDefault(ArkadeSwapMetadataKeys.EvmLockProvenBlockTimestamp);
        return observed is null || proven is null || !long.TryParse(timestamp, out var provenAt) ? null
            : new ArkCompositionEvmLockProof(null, observed, proven, provenAt);
    }
}

/// <summary>Public prepared RFQ, optional quote and observed Arkade funding.</summary>
/// <param name="RfqId">Globally reserved correlation id.</param>
/// <param name="Kind">Outgoing or Ingress.</param>
/// <param name="Quote">Accepted public quote, absent while prepared.</param>
/// <param name="FundingTransactionId">Transaction proving L or M funding.</param>
/// <param name="FundedAmountSats">Observed Arkade amount.</param>
public sealed record ArkCompositionLegData(string RfqId, string Kind, ArkCompositionQuote? Quote,
    string? FundingTransactionId, long? FundedAmountSats);
