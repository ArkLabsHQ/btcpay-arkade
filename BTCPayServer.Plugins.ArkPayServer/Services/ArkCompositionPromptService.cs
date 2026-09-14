using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Contracts;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Composition;
using NArk.ArkadeIntents.Evm;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Onchain;
using NArk.ArkadeIntents.Rfq;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Contracts;
using NArk.Core.Transport;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>Prompt-facing projection of an index row plus its SDK-quoted legs.</summary>
public sealed record ArkCompositionPrompt(Guid RouteId, string StoreId, string? InvoiceId, string PaymentMethodId,
    string WalletId, string OutgoingSwapId, string? IngressSwapId, string PaymentHash, long BaseAmountSats,
    long IngressFeeSats, string CustomerDestination, long CheckoutExpiresAt, DateTimeOffset CreatedAt)
{
    public static ArkCompositionPrompt From(ArkCompositionRoute route, long ingressFeeSats) => new(route.RouteId,
        route.StoreId, route.InvoiceId, route.PaymentMethodId, route.WalletId, route.OutgoingSwapId,
        route.IngressSwapId, route.PaymentHash, route.BaseAmountSats, ingressFeeSats, route.CustomerDestination!,
        route.CheckoutExpiresAt!.Value, route.CreatedAt);
}

/// <summary>
/// Creates prompts by quoting independent SDK legs and indexing the resulting route.
/// Swap state lives in the SDK's intent storage; this service keeps only the
/// store-scoped invoice index and BTCPay prompt projections.
/// </summary>
public sealed class ArkCompositionPromptService(ArkCompositionRouteRepository repository,
    IArkadeIntentStorage intents, IArkCompositionContextSource? contextSource = null,
    ArkCompositionSolverFactory? solvers = null, ArkCompositionEvmContextFactory? evmContexts = null,
    IClientTransport? ark = null, IContractService? contracts = null,
    LightningIntentsClient? lightning = null, OnchainIntentsClient? onchain = null,
    ArkadeSolverService? claimRecipients = null, IOptions<ArkadeIntentsOptions>? options = null,
    IArkCompositionExecutionLock? executionLock = null, ArkCompositionCheckoutPolicy? checkoutPolicy = null,
    ComposedSwapOptions? timingOptions = null, TimeProvider? timeProvider = null)
{
    internal bool ExecutionAvailable => executionLock?.SupportsCrossProcessExecution == true;

    internal Task<InvoiceEntity?> FindInvoiceAsync(string invoiceId) => contextSource?.FindInvoiceAsync(invoiceId)
        ?? throw new InvalidOperationException("The composition invoice context is unavailable.");

    internal Task<ArkCompositionRoute?> FindRouteAsync(string storeId, Guid routeId,
        CancellationToken cancellationToken) => repository.Get(storeId, routeId, cancellationToken);

    internal static bool Enabled(StoreData store, string rail) => Configuration(store)?.EvmSettlement is
        { Enabled: true, RoutePolicy: { } policy } && policy.EnabledSourceRails.Contains(rail);

    internal async Task<ArkCompositionPrompt?> TryCreateLightningAsync(string storeId, string walletId, long amountSats,
        TimeSpan expiry, CancellationToken cancellationToken)
    {
        var store = contextSource is null ? null : await contextSource.FindStoreAsync(storeId);
        if (store is null) throw new InvalidOperationException("The composition store context is unavailable.");
        return await TryCreateAsync(store, null, "BTC-LN", amountSats,
            (timeProvider ?? TimeProvider.System).GetUtcNow().Add(expiry), walletId, cancellationToken);
    }

    internal async Task AttachLightningAsync(StoreData store, InvoiceEntity invoice, string? lightningInvoiceId,
        string? paymentHash, string? destination, CancellationToken cancellationToken)
    {
        if (store.Id != invoice.StoreId)
            throw new InvalidOperationException("The Lightning prompt invoice does not belong to its store.");
        if (!Guid.TryParseExact(lightningInvoiceId, "N", out var routeId) || string.IsNullOrWhiteSpace(paymentHash)) return;
        var route = await repository.Get(store.Id, routeId, cancellationToken);
        if (route is null) return;
        if (route.PaymentMethodId != "BTC-LN" || route.PaymentHash != paymentHash.ToLowerInvariant() ||
            route.CustomerDestination != destination)
            throw new InvalidOperationException("The Lightning prompt does not match its composed route.");
        await repository.AttachInvoice(store.Id, routeId, invoice.Id, cancellationToken);
        checkoutPolicy?.RememberStore(store);
        var attached = await repository.Get(store.Id, routeId, cancellationToken);
        if (attached is not null) checkoutPolicy?.RememberRoute(attached);
    }

    internal async Task<LightningInvoice?> FindLightningAsync(string? storeId, string walletId, string id,
        CancellationToken cancellationToken)
    {
        if (storeId is null) return null;
        var route = Guid.TryParse(id, out var routeId) ? await repository.Get(storeId, routeId, cancellationToken)
            : id.Length == 64 && id.All(char.IsAsciiHexDigit)
                ? await repository.FindByPaymentHash(storeId, id, cancellationToken) : null;
        if (route is not { PaymentMethodId: "BTC-LN", CustomerDestination: not null } || route.WalletId != walletId)
            return null;
        return await ToLightningInvoiceAsync(route, cancellationToken);
    }

    internal async Task<bool> IsCompositionIntentAsync(string walletId, string id, string? paymentHash,
        CancellationToken cancellationToken)
    {
        var intent = await intents.GetArkadeSwapIntent(id, cancellationToken);
        return intent is not null && intent.WalletId == walletId &&
            (paymentHash is null || string.Equals(intent.PaymentHash, paymentHash, StringComparison.OrdinalIgnoreCase)) &&
            (intent.Metadata.ContainsKey(ArkadeSwapMetadataKeys.ComposedOutgoingSwapId) ||
             intent.Metadata.ContainsKey(ArkadeSwapMetadataKeys.ComposedPayoutPkScript));
    }

    internal async Task<IReadOnlyList<LightningInvoice>> ListLightningAsync(string? storeId, string walletId,
        CancellationToken cancellationToken)
    {
        if (storeId is null) return [];
        var invoices = new List<LightningInvoice>();
        for (var skip = 0; ; skip = checked(skip + 100))
        {
            var page = await repository.List(storeId, paymentMethodId: "BTC-LN", skip: skip, take: 100,
                cancellationToken: cancellationToken);
            foreach (var route in page.Where(r => r.WalletId == walletId && r.CustomerDestination is not null))
            {
                var invoice = await ToLightningInvoiceAsync(route, cancellationToken);
                if (invoice is not null) invoices.Add(invoice);
            }
            if (page.Count < 100) break;
        }
        return invoices.OrderByDescending(i => i.ExpiresAt).ToArray();
    }

    internal LightningInvoice ToLightningInvoice(ArkCompositionPrompt prompt)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new LightningInvoice
        {
            Id = prompt.RouteId.ToString("N"), PaymentHash = prompt.PaymentHash,
            BOLT11 = prompt.CustomerDestination,
            Amount = LightMoney.Satoshis(checked(prompt.BaseAmountSats + prompt.IngressFeeSats)),
            ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(prompt.CheckoutExpiresAt),
            Status = prompt.CheckoutExpiresAt <= now.ToUnixTimeSeconds()
                ? LightningInvoiceStatus.Expired : LightningInvoiceStatus.Unpaid
        };
    }

    internal async Task<LightningInvoice?> ToLightningInvoiceAsync(ArkCompositionRoute route,
        CancellationToken cancellationToken)
    {
        if (route.PaymentMethodId != "BTC-LN" || route.CustomerDestination is null) return null;
        var outgoing = await intents.GetArkadeSwapIntent(route.OutgoingSwapId, cancellationToken);
        var ingress = route.IngressSwapId is null ? null
            : await intents.GetArkadeSwapIntent(route.IngressSwapId, cancellationToken);
        if (outgoing is null || outgoing.PaymentHash != route.PaymentHash) return null;
        var fee = ingress is null ? 0 : checked(ingress.OfferAmount.Satoshi - ingress.WantAmount.Satoshi);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        return new LightningInvoice
        {
            Id = route.RouteId.ToString("N"), PaymentHash = route.PaymentHash, BOLT11 = route.CustomerDestination,
            Amount = LightMoney.Satoshis(checked(route.BaseAmountSats + fee)),
            ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(route.CheckoutExpiresAt ?? now),
            Status = outgoing.Metadata.ContainsKey(ArkadeSwapMetadataKeys.EvmClaimTxid) ? LightningInvoiceStatus.Paid
                : route.CheckoutExpiresAt <= now ? LightningInvoiceStatus.Expired : LightningInvoiceStatus.Unpaid
        };
    }

    /// <summary>Returns null for a non-composed rail; quotes independent SDK legs and indexes the route.</summary>
    public async Task<ArkCompositionPrompt?> TryCreateAsync(StoreData store, InvoiceEntity? invoice, string rail,
        long baseAmountSats, DateTimeOffset expiresAt, string? expectedWalletId = null, CancellationToken cancellationToken = default)
    {
        checkoutPolicy?.RememberStore(store);
        var configuration = Configuration(store);
        if (configuration?.EvmSettlement is not { Enabled: true, RoutePolicy: { } policy } || !policy.EnabledSourceRails.Contains(rail))
            return null;
        if (expectedWalletId is not null && expectedWalletId != configuration.WalletId)
            throw new InvalidOperationException("The Lightning wallet does not match this store's composition policy.");
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (baseAmountSats <= 0 || expiresAt <= now)
            throw new InvalidOperationException("The payment prompt has no payable amount or checkout window.");
        if (solvers is null || evmContexts is null || ark is null || contracts is null || lightning is null ||
            onchain is null || (rail != "ARKADE" && claimRecipients is null))
            throw new ArkCompositionUnavailableException("sdk-composition-unavailable");
        if (!ExecutionAvailable)
            throw new ArkCompositionUnavailableException("cross-process-execution-lock-unavailable");

        var validated = policy.Validate();
        var server = await ark.GetServerInfoAsync(cancellationToken);
        var networkName = server.NetworkName
            ?? throw new InvalidOperationException("The Ark server did not report a network.");
        using var context = await evmContexts.OpenAsync(store.Id, configuration.WalletId, configuration.EvmSettlement.AssetId,
            configuration.EvmSettlement.Destination, validated with { EnabledSourceRails = [rail] }, cancellationToken);
        using var outgoingSolver = await solvers.OpenAsync(validated.OutgoingSolver!, networkName,
            configuration.EvmSettlement.AssetId, "ARKADE", baseAmountSats, cancellationToken);

        var composer = new ComposedSwapClient(
            new EvmIntentsClient(ark, contracts, intents, context.Rpc, options), lightning, onchain,
            timingOptions, timeProvider);
        var outgoingRfqId = RfqProtocol.NewRfqId();
        var ingressRfqId = rail == "ARKADE" ? null : RfqProtocol.NewRfqId();

        string outgoingSwapId, ingressSwapId, paymentHash, customerDestination;
        long checkoutExpiresAt, ingressFeeSats;
        if (rail == "ARKADE")
        {
            var route = await composer.CreateArkadeAsync(configuration.WalletId, baseAmountSats,
                configuration.EvmSettlement.Destination, context.Policy, outgoingSolver.Transport,
                outgoingRfqId, outgoingSolver.Card, cancellationToken);
            outgoingSolver.VerifyIdentity(route.Outgoing.Quote.SolverPubkey);
            (outgoingSwapId, ingressSwapId, paymentHash, customerDestination, checkoutExpiresAt, ingressFeeSats) = (
                route.Outgoing.RfqId, null, route.Outgoing.Secret.PaymentHash, route.Outgoing.LockupAddress,
                Math.Min(expiresAt.ToUnixTimeSeconds(), route.CheckoutExpiresAt), 0);
        }
        else
        {
            using var ingressSolver = await solvers.OpenAsync(
                rail == "BTC-LN" ? validated.LightningIngressSolver! : validated.OnchainIngressSolver!,
                networkName, configuration.EvmSettlement.AssetId, rail, baseAmountSats, cancellationToken);
            var recipient = await claimRecipients!.ResolveClaimRecipientAsync(cancellationToken);
            if (rail == "BTC-LN")
            {
                var route = await composer.CreateLightningAsync(configuration.WalletId, baseAmountSats,
                    configuration.EvmSettlement.Destination, context.Policy, outgoingSolver.Transport,
                    ingressSolver.Transport, recipient, outgoingRfqId, ingressRfqId,
                    outgoingSolver.Card, ingressSolver.Card, cancellationToken);
                outgoingSolver.VerifyIdentity(route.Outgoing.Quote.SolverPubkey);
                ingressSolver.VerifyIdentity(route.Ingress.Quote.SolverPubkey);
                (outgoingSwapId, ingressSwapId, paymentHash, customerDestination, checkoutExpiresAt, ingressFeeSats) = (
                    route.Outgoing.RfqId, route.Ingress.RfqId, route.Outgoing.Secret.PaymentHash, route.Ingress.Invoice,
                    Math.Min(expiresAt.ToUnixTimeSeconds(), route.CheckoutExpiresAt), route.IngressFeeSats);
            }
            else
            {
                var refundAddress = await OnchainRefundAddressAsync(configuration.WalletId, server.Network,
                    cancellationToken);
                var route = await composer.CreateOnchainAsync(configuration.WalletId, baseAmountSats,
                    configuration.EvmSettlement.Destination, context.Policy, outgoingSolver.Transport,
                    ingressSolver.Transport, recipient, refundAddress, outgoingRfqId, ingressRfqId,
                    outgoingSolver.Card, ingressSolver.Card, cancellationToken);
                outgoingSolver.VerifyIdentity(route.Outgoing.Quote.SolverPubkey);
                ingressSolver.VerifyIdentity(route.Ingress.Quote.SolverPubkey);
                (outgoingSwapId, ingressSwapId, paymentHash, customerDestination, checkoutExpiresAt, ingressFeeSats) = (
                    route.Outgoing.RfqId, route.Ingress.RfqId, route.Outgoing.Secret.PaymentHash, route.Ingress.HtlcAddress,
                    Math.Min(expiresAt.ToUnixTimeSeconds(), route.CheckoutExpiresAt), route.IngressFeeSats);
            }
        }

        var created = ArkCompositionRoute.Create(store.Id, invoice?.Id, rail, configuration.WalletId,
            outgoingSwapId, ingressSwapId, paymentHash, baseAmountSats,
            customerDestination, checkoutExpiresAt, now);
        await repository.Add(store.Id, created, cancellationToken);
        checkoutPolicy?.RememberRoute(created);
        return ArkCompositionPrompt.From(created, ingressFeeSats);
    }

    private async Task<BitcoinAddress> OnchainRefundAddressAsync(string walletId, Network network,
        CancellationToken cancellationToken)
    {
        var boarding = await contracts!.DeriveContract(walletId, NextContractPurpose.Boarding,
                cancellationToken: cancellationToken) as ArkBoardingContract
            ?? throw new InvalidOperationException("A tracked onchain refund destination is required.");
        return boarding.GetOnchainAddress(network);
    }

    internal static ArkadePaymentMethodConfig? Configuration(StoreData store) =>
        store.GetPaymentMethodConfigs(true).TryGetValue(ArkadePlugin.ArkadePaymentMethodId, out var value)
            ? value.ToObject<ArkadePaymentMethodConfig>(BlobSerializer.CreateSerializer().Serializer)
            : null;
}

public sealed class ArkCompositionUnavailableException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}
