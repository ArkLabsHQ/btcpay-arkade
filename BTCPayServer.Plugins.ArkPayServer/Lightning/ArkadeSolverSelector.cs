using NArk.ArkadeIntents.Services;
using NArk.ArkadeIntents.SolverRegistry;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>Where to meet a solver, and the terms it published for meeting there.</summary>
/// <param name="Pubkey">The solver's x-only discovery key — who to address.</param>
/// <param name="Relay">The relay to meet on — where to dial.</param>
/// <param name="Market">The market that was chosen, for its limits and its price.</param>
public sealed record SolverRendezvous(string Pubkey, Uri Relay, IndexedMarket? Market);

/// <summary>
/// Chooses which solver this store's Lightning corridor trades with.
/// </summary>
/// <remarks>
/// <para>
/// Three questions, in order: does a solver serve the Lightning corridor at all, does it serve a
/// trade this size, and of the ones that do, which is cheapest. The registry answers all three
/// without a negotiation, because a card states its corridor, its bounds and its fee up front.
/// </para>
/// <para>
/// Configuration still wins where it is set. A named solver is how a development stack works — its
/// solver mints a fresh identity per run, so no registry can list it — and it is also the escape
/// hatch for an operator who wants one particular counterparty rather than the cheapest.
/// </para>
/// </remarks>
public sealed class ArkadeSolverSelector(
    ArkadeSolverOptions options,
    string? networkName,
    SolverDiscoveryService? discovery = null)
{
    /// <summary>The rail this plugin's Lightning corridor settles its quote side on.</summary>
    private const string LightningCorridor = "lightning";

    /// <summary>Both legs of the corridor are bitcoin; only the rails differ.</summary>
    private const string BitcoinAssetId = "btc";

    /// <summary>Whether a solver was named outright, making discovery unnecessary.</summary>
    public bool HasExplicitSolver =>
        !string.IsNullOrWhiteSpace(options.RelayUri) && !string.IsNullOrWhiteSpace(options.SolverPubkey);

    /// <summary>Whether this deployment could reach a solver at all, by either route.</summary>
    /// <remarks>
    /// Cheap and synchronous on purpose: it answers "is this deployment wired for Lightning", which
    /// several callers ask while rendering a page. Whether a solver is actually listed, and serves a
    /// given size, is <see cref="SelectAsync"/>'s question and needs the network.
    /// </remarks>
    public bool CanReachASolver => HasExplicitSolver || (discovery is not null && networkName is not null);

    /// <summary>Pick the cheapest listed solver that serves a Lightning trade of this size.</summary>
    /// <param name="amountSats">The size being traded, on the Arkade side.</param>
    /// <param name="cancellationToken">Cancels the registry fetch.</param>
    /// <returns>Where to meet the chosen solver, or <c>null</c> when none serves this trade.</returns>
    public async Task<SolverRendezvous?> SelectAsync(
        long amountSats, CancellationToken cancellationToken = default)
    {
        if (HasExplicitSolver && Uri.TryCreate(options.RelayUri, UriKind.Absolute, out var configured))
        {
            return new SolverRendezvous(options.SolverPubkey!, configured, null);
        }

        var market = (await ServingMarketsAsync(amountSats, cancellationToken)).FirstOrDefault();
        if (market?.DiscoveryPubkey is not { Length: > 0 } pubkey)
        {
            return null;
        }

        // A market that survived selection is reachable by construction, but the relay list is a
        // stranger's data and an unparseable entry should drop the candidate rather than throw.
        return market.Transports?.Nostr?.Relays
            .Select(r => Uri.TryCreate(r, UriKind.Absolute, out var parsed) ? parsed : null)
            .FirstOrDefault(r => r is not null) is { } relay
            ? new SolverRendezvous(pubkey, relay, market)
            : null;
    }

    /// <summary>The widest size range any listed solver serves on this corridor.</summary>
    /// <param name="cancellationToken">Cancels the registry fetch.</param>
    /// <returns>The range, or <c>null</c> when nothing can be said about it.</returns>
    /// <remarks>
    /// The union rather than one solver's range, because the choice of solver is made per trade: an
    /// amount only one of them serves is still an amount this store can be paid. Used to advertise a
    /// range up front — an LNURL offer, a checkout — where there is no amount to select on yet.
    /// A named solver publishes nothing, so it constrains nothing here.
    /// </remarks>
    public async Task<(long Min, long Max)?> ServedRangeAsync(CancellationToken cancellationToken = default)
    {
        if (HasExplicitSolver || discovery is null || networkName is null)
        {
            return null;
        }

        var corridors = (await DiscoverAsync(cancellationToken))
            .Where(m => m.PairKey() == WantedPair && m.MaxBaseAmount > 0)
            .ToList();

        return corridors.Count == 0
            ? null
            : (corridors.Min(m => m.MinBaseAmount), corridors.Max(m => m.MaxBaseAmount));
    }

    /// <summary>Whether any listed solver serves a Lightning trade of this size.</summary>
    /// <param name="amountSats">The size being traded.</param>
    /// <param name="cancellationToken">Cancels the registry fetch.</param>
    public async Task<bool> ServesAsync(long amountSats, CancellationToken cancellationToken = default) =>
        HasExplicitSolver || (await ServingMarketsAsync(amountSats, cancellationToken)).Count > 0;

    /// <summary>The corridor's canonical identity: arkade bitcoin against Lightning bitcoin.</summary>
    private static string WantedPair =>
        $"{SolverMarket.ArkadeCorridor}:{BitcoinAssetId}/{LightningCorridor}:{BitcoinAssetId}";

    private async Task<IReadOnlyList<IndexedMarket>> ServingMarketsAsync(
        long amountSats, CancellationToken cancellationToken)
    {
        if (discovery is null || networkName is null)
        {
            return [];
        }

        var markets = await DiscoverAsync(cancellationToken);

        // Corridor, then size, then cost — FilterAndRank does all three, and ranks on the total fee
        // at this size rather than on the spread.
        return SolverDiscoveryService
            .FilterAndRank(markets, BitcoinAssetId, BitcoinAssetId, amountSats, quoteCorridor: LightningCorridor)
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
            // A registry that cannot be fetched means no solver was found, not a failed payment: the
            // caller withdraws the offer, which is the same thing it does for an empty registry.
            return [];
        }
    }

    /// <summary>The registry's name for a chain, or <c>null</c> when it publishes none.</summary>
    /// <param name="network">The chain this deployment runs on.</param>
    /// <remarks>
    /// Kept here rather than taken from <c>ChainName.ToString()</c> because the two vocabularies only
    /// coincide by accident: the registry says <c>bitcoin</c> where NBitcoin says <c>Main</c>.
    /// </remarks>
    public static string? RegistryNetworkName(ChainName network) =>
        network == NBitcoin.Bitcoin.Instance.Mainnet.ChainName ? "bitcoin"
        : network == NBitcoin.Bitcoin.Instance.Signet.ChainName ? "signet"
        : network == NBitcoin.Bitcoin.Instance.Mutinynet.ChainName ? "mutinynet"
        : network == ChainName.Regtest ? "regtest"
        : null;
}
