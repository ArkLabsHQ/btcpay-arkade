using BTCPayServer.Plugins.ArkPayServer.Data;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Composition;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Onchain;
using NArk.Core.Transport;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Advances index rows through the SDK's composed execution client.
/// Owns no swap state: quotes, funding and EVM proofs live in SDK intent storage.
/// </summary>
public sealed class ArkComposedSwapExecutionService(ArkCompositionRouteRepository routes,
    IArkadeIntentStorage intents, IVtxoStorage vtxos, IBitcoinBlockchain blockchain,
    IContractStorage contracts, IClientTransport transport, LightningIntentsClient lightning,
    ArkCompositionEvmContextFactory evmContexts, IArkCompositionExecutionLock executionLock,
    IArkCompositionPaymentSink payments, OnchainIntentsClient? onchain = null,
    TimeProvider? timeProvider = null, ILogger<ArkComposedSwapExecutionService>? logger = null)
{
    public async Task AdvanceAsync(string storeId, Guid routeId, CancellationToken cancellationToken = default)
    {
        await using var lease = await executionLock.AcquireAsync($"route:{storeId}:{routeId:N}", cancellationToken);
        var route = await routes.Get(storeId, routeId, cancellationToken);
        if (route is null) return;
        var outgoing = await intents.GetArkadeSwapIntent(route.OutgoingSwapId, cancellationToken)
            ?? throw new InvalidOperationException("The outgoing SDK intent is unavailable.");
        if (outgoing.WalletId != route.WalletId || outgoing.PaymentHash != route.PaymentHash)
            throw new InvalidOperationException("The outgoing SDK intent differs from the indexed route.");

        using var context = await evmContexts.OpenForExecutionAsync(route.StoreId, route.WalletId,
            EvmAssetId(outgoing), EvmDestination(outgoing), route.PaymentMethodId,
            EvmSwapContract(outgoing), cancellationToken);
        var client = new ComposedSwapExecutionClient(intents, vtxos, blockchain, contracts, transport,
            lightning, onchain, context.Rpc, context.Sender, context.Policy, timeProvider);
        var result = await client.AdvanceAsync(route.OutgoingSwapId, route.IngressSwapId, cancellationToken);
        if (result.OutgoingSwapId != route.OutgoingSwapId || result.IngressSwapId != route.IngressSwapId)
            throw new InvalidOperationException("The SDK execution result identifies a different route.");
        if (result.EvmClaimTxid is not null && route.InvoiceId is not null)
            await payments.SettleAsync(route, result, cancellationToken);
        else if (result.EvmClaimTxid is null)
            logger?.LogDebug("Composition route {RouteId} awaits funding or EVM settlement.", route.RouteId);
    }

    private static string EvmAssetId(ArkadeSwapIntent outgoing) => outgoing.ToAssetId
        ?? throw new InvalidOperationException("The outgoing SDK intent carries no EVM asset.");

    private static string EvmDestination(ArkadeSwapIntent outgoing) =>
        outgoing.EvmMetadata().ClaimAddress;

    private static string EvmSwapContract(ArkadeSwapIntent outgoing) =>
        outgoing.EvmMetadata().SwapContractAddress;
}
