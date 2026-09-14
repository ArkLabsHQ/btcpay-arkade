using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Services;
using NArk.ArkadeIntents.SolverRegistry;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public sealed class ArkCompositionSolverFactory(SolverDiscoveryService discovery, IHttpClientFactory http)
{
    public async Task<ArkCompositionSolverConnection> OpenAsync(ArkEvmSolverSelection selection, string network,
        string assetId, string rail, long amountSats, CancellationToken cancellationToken)
    {
        selection = selection.Validate();
        if (selection.Mode == "explicit") return Open(new Uri(selection.Endpoint!), selection.SolverPubkey, null);
        var markets = await discovery.DiscoverMarketsAsync(network, cancellationToken: cancellationToken);
        var selected = Select(markets, selection, network, assetId, rail, amountSats)
            ?? throw new InvalidOperationException("No registry solver serves this route and amount.");
        foreach (var relay in selected.Transports!.Nostr!.Relays)
        {
            try
            {
                var endpoint = new ArkEvmSolverSelection("explicit", relay, selected.DiscoveryPubkey).Validate();
                if (new Uri(endpoint.Endpoint!).Scheme is not ("ws" or "wss")) continue;
                return Open(new Uri(endpoint.Endpoint!), endpoint.SolverPubkey, LegacyCard(selected));
            }
            catch (ArgumentException) { }
        }
        throw new InvalidOperationException("The selected registry solver has no usable relay.");
    }

    public static IndexedMarket? Select(IEnumerable<IndexedMarket> markets, ArkEvmSolverSelection selection,
        string network, string assetId, string rail, long amountSats) => markets
        .Where(m => Matches(m, network, assetId, rail)
            && m.MinBaseAtomicAmount <= amountSats && m.MaxBaseAtomicAmount >= amountSats && m.MaxBaseAtomicAmount > 0
            && m.DiscoveryPubkey is { Length: 64 } && m.Transports?.Nostr?.Relays.Count > 0
            && (selection.DiscoveryPubkey is null || selection.DiscoveryPubkey == m.DiscoveryPubkey))
        .OrderBy(m => m.TotalFeeOn(amountSats)).ThenBy(m => m.DiscoveryPubkey, StringComparer.Ordinal).FirstOrDefault();

    private static bool Matches(IndexedMarket market, string network, string assetId, string rail)
    {
        try
        {
            var expectedBase = AssetIdentifier.FromLegacy("btc", network).Value;
            var expectedQuote = rail == "ARKADE" ? assetId
                : AssetIdentifier.FromLegacy("btc", network, rail == "BTC-LN" ? "lightning" : "onchain").Value;
            var baseId = market.BaseAsset.CanonicalId.Contains('/') ? market.BaseAsset.CanonicalId
                : AssetIdentifier.FromLegacy(market.BaseAsset.Id, network, market.CorridorOf(MarketSide.Base)).Value;
            var quoteId = market.QuoteAsset.CanonicalId.Contains('/') ? market.QuoteAsset.CanonicalId
                : AssetIdentifier.FromLegacy(market.QuoteAsset.Id, network, market.CorridorOf(MarketSide.Quote)).Value;
            return baseId == expectedBase && quoteId == expectedQuote;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or NotSupportedException) { return false; }
    }

    private static SolverCard LegacyCard(IndexedMarket market)
    {
        var evm = market.QuoteAsset.CanonicalId.StartsWith("eip155:", StringComparison.Ordinal);
        var quoteId = evm ? AssetIdentifier.Parse(market.QuoteAsset.CanonicalId).Asset["erc20:".Length..] : "btc";
        return new SolverCard
        {
            Name = market.Solver, DiscoveryPubkey = market.DiscoveryPubkey, Transports = market.Transports,
            Markets = [new SolverMarket
            {
                BaseAsset = new AssetDescriptor { Id = "btc" }, QuoteAsset = new AssetDescriptor { Id = quoteId },
                BaseCorridor = "arkade", QuoteCorridor = evm ? "ethereum" : market.CorridorOf(MarketSide.Quote),
                FeeBps = market.FeeBps, FeeFlat = market.FeeFlat,
                MinBaseAtomicAmount = market.MinBaseAtomicAmount, MaxBaseAtomicAmount = market.MaxBaseAtomicAmount,
                MinQuoteAtomicAmount = market.MinQuoteAtomicAmount, MaxQuoteAtomicAmount = market.MaxQuoteAtomicAmount
            }]
        };
    }

    private ArkCompositionSolverConnection Open(Uri endpoint, string? pin, SolverCard? card)
    {
        if (endpoint.Scheme is "http" or "https")
        {
            var client = http.CreateClient("ArkCompositionRfq");
            return new ArkCompositionSolverConnection(new HttpRfqTransport(client, endpoint), card, pin, client,
                new ArkEvmSolverSelection("explicit", endpoint.AbsoluteUri, pin));
        }
        var transport = new NostrRfqTransport(endpoint, pin!);
        return new ArkCompositionSolverConnection(transport, card, null, transport,
            new ArkEvmSolverSelection("explicit", endpoint.AbsoluteUri, pin));
    }
}

public sealed class ArkCompositionSolverConnection(IRfqTransport transport, SolverCard? card, string? settlementPin,
    IDisposable lifetime, ArkEvmSolverSelection selection) : IDisposable
{
    public IRfqTransport Transport { get; } = transport;
    public SolverCard? Card { get; } = card;
    public ArkEvmSolverSelection Selection { get; } = selection;
    public void VerifyIdentity(string settlementKey)
    {
        if (settlementPin is not null && settlementPin != settlementKey)
            throw new InvalidOperationException("The solver quote does not match its configured settlement identity.");
    }
    public void Dispose() => lifetime.Dispose();
}
