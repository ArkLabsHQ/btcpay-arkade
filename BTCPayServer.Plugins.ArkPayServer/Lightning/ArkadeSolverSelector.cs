using NArk.ArkadeIntents.Services;
using NArk.ArkadeIntents.SolverRegistry;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

public sealed record SolverRendezvous(string Pubkey, Uri Relay, IndexedMarket? Market)
{
    // A one-market card, so the SDK holds the quote to the terms this market was selected on.
    public SolverCard? Card => Market is null ? null : new SolverCard
    {
        Name = Market.Solver,
        DiscoveryPubkey = Market.DiscoveryPubkey,
        Transports = Market.Transports,
        Markets = [Market],
    };
}

/// <summary>Picks the cheapest listed solver for a corridor; a solver named in configuration always wins.</summary>
public sealed class ArkadeSolverSelector(
    ArkadeSolverOptions options,
    string? networkName,
    SolverDiscoveryService? discovery = null)
{
    public const string LightningCorridor = "lightning";

    public const string OnchainCorridor = "onchain";

    // A relay carries everyone's traffic, so a key is what picks one counterparty out of it; an http(s)
    // endpoint is already one counterparty, and asking for a key there is a step with nothing behind it.
    public bool HasExplicitSolver =>
        !string.IsNullOrWhiteSpace(options.RelayUri)
        && (!string.IsNullOrWhiteSpace(options.SolverPubkey) || IsHttp(options.RelayUri));

    private static bool IsHttp(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

    public bool CanReachASolver => HasExplicitSolver || (discovery is not null && networkName is not null);

    // The amount ranks but does not exclude: which card bound applies depends on trade direction, and
    // guessing wrong would silently admit or refuse sizes. The quote refuses with the solver's own reason.
    public Task<SolverRendezvous?> SelectAsync(
        long amountSats, CancellationToken cancellationToken = default) =>
        SelectAsync(amountSats, LightningCorridor, cancellationToken);

    public async Task<SolverRendezvous?> SelectAsync(
        long amountSats, string quoteCorridor, CancellationToken cancellationToken = default)
    {
        if (HasExplicitSolver && Uri.TryCreate(options.RelayUri, UriKind.Absolute, out var configured))
        {
            return new SolverRendezvous(options.SolverPubkey ?? "", configured, null);
        }

        return (await CandidatesAsync(amountSats, quoteCorridor, cancellationToken)).FirstOrDefault();
    }

    /// <summary>
    /// Every way to reach a solver for this corridor, cheapest first, then each solver's relays in the
    /// order it lists them. One unreachable relay — or one solver that is down — is then a step to the
    /// next rather than the end of the attempt.
    /// </summary>
    public async Task<IReadOnlyList<SolverRendezvous>> CandidatesAsync(
        long amountSats, string quoteCorridor, CancellationToken cancellationToken = default)
    {
        if (HasExplicitSolver && Uri.TryCreate(options.RelayUri, UriKind.Absolute, out var configured))
        {
            return [new SolverRendezvous(options.SolverPubkey ?? "", configured, null)];
        }

        return
        [
            .. (await MarketsAsync(quoteCorridor, cancellationToken))
                .OrderBy(m => m.TotalFeeOn(amountSats))
                .ThenBy(m => m.FeeBps)
                .Where(m => m.DiscoveryPubkey is { Length: > 0 })
                // The relay list is a stranger's data; an unparseable entry drops that relay, not the solver.
                .SelectMany(m => (m.Transports?.Nostr?.Relays ?? [])
                    .Select(r => Uri.TryCreate(r, UriKind.Absolute, out var parsed) ? parsed : null)
                    .Where(r => r is not null)
                    .Select(r => new SolverRendezvous(m.DiscoveryPubkey!, r!, m)))
        ];
    }

    public Task<bool> HasLightningSolverAsync(CancellationToken cancellationToken = default) =>
        HasSolverForAsync(LightningCorridor, cancellationToken);

    // A named solver publishes no card, so it is taken at its word for every corridor.
    public async Task<bool> HasSolverForAsync(
        string quoteCorridor, CancellationToken cancellationToken = default) =>
        HasExplicitSolver || (await MarketsAsync(quoteCorridor, cancellationToken)).Count > 0;

    // Quote side = the Lightning leg the payer sends; the base side includes the fee and would mislead.
    // Union across solvers, since the choice is made per payment.
    public Task<(long Min, long Max)?> ServedRangeAsync(CancellationToken cancellationToken = default) =>
        ServedRangeAsync(LightningCorridor, cancellationToken);

    public async Task<(long Min, long Max)?> ServedRangeAsync(
        string quoteCorridor, CancellationToken cancellationToken = default)
    {
        if (HasExplicitSolver)
        {
            return null;
        }

        var bounded = (await MarketsAsync(quoteCorridor, cancellationToken))
            .Where(m => m.MaxQuoteAmount > 0)
            .ToList();

        return bounded.Count == 0
            ? null
            : (bounded.Min(m => m.MinQuoteAmount), bounded.Max(m => m.MaxQuoteAmount));
    }

    // Matched on meaning, not on PairKey: a legacy card keys as "arkade:btc/lightning:btc", a CAIP-19 one as
    // "arkade:<network>/slip44:N/bolt11:<network>/slip44:N", and the index carries both.
    private static bool IsBitcoinCorridor(SolverMarket market, string quoteCorridor)
    {
        try
        {
            return market.CorridorOf(MarketSide.Base) == SolverMarket.ArkadeCorridor
                   && market.CorridorOf(MarketSide.Quote) == quoteCorridor
                   && market.IsSameAsset
                   && market.BaseAsset.LegacyId == "btc";
        }
        catch (Exception e) when (e is FormatException or NotSupportedException)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<IndexedMarket>> MarketsAsync(
        string quoteCorridor, CancellationToken cancellationToken)
    {
        if (discovery is null || networkName is null)
        {
            return [];
        }

        return (await DiscoverAsync(cancellationToken))
            .Where(m => IsBitcoinCorridor(m, quoteCorridor))
            .Where(m => m.DiscoveryPubkey is { Length: > 0 } && m.Transports?.Nostr?.Relays.Count > 0)
            .ToList();
    }

    private async Task<IReadOnlyList<IndexedMarket>> DiscoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await discovery!.DiscoverMarketsAsync(networkName!, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The registry client already falls back to its last good copy; no solver means the offer is withdrawn.
            return [];
        }
    }

    // The registry says "bitcoin" where NBitcoin says "Main", so ChainName.ToString() won't do.
    public static string? RegistryNetworkName(ChainName network) =>
        network == NBitcoin.Bitcoin.Instance.Mainnet.ChainName ? "bitcoin"
        : network == NBitcoin.Bitcoin.Instance.Signet.ChainName ? "signet"
        : network == NBitcoin.Bitcoin.Instance.Mutinynet.ChainName ? "mutinynet"
        : network == ChainName.Regtest ? "regtest"
        : null;
}
