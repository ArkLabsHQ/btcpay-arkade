using System.Net.Http.Json;
using System.Text.Json;
using NArk.ArkadeIntents.Rfq;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>
/// The solver this deployment trades Lightning corridors with, and how to reach it.
/// </summary>
/// <remarks>
/// <para>
/// Which solver is not fixed here: <see cref="ArkadeSolverSelector"/> chooses one per trade from the
/// public registry, or takes the one configuration names. This owns what happens once that choice is
/// made — opening the right transport for it, and closing it afterwards.
/// </para>
/// <para>
/// A claim daemon is configured the same way and for the same reason — no card carries its key —
/// but unlike the solver it is optional. See <see cref="ResolveClaimRecipientAsync"/>.
/// </para>
/// </remarks>
public class ArkadeSolverService(
    ArkadeSolverOptions options,
    ArkadeSolverSelector selector,
    IHttpClientFactory httpClientFactory)
{
    private string? _covclaimdKey;

    /// <summary>Whether this deployment can reach a solver at all.</summary>
    /// <remarks>
    /// Either route counts: a solver named in configuration, or a registry to discover one in. It
    /// says nothing about whether a solver is listed or serves a given size — that needs the network
    /// and an amount, and is <see cref="ServesAsync"/>'s question. This is the cheap synchronous
    /// answer several callers need while rendering a page.
    /// </remarks>
    public bool IsConfigured => selector.CanReachASolver;

    /// <summary>Whether a solver will trade a Lightning swap of this size.</summary>
    /// <param name="amountSats">The size being traded.</param>
    /// <param name="cancellationToken">Cancels the registry fetch.</param>
    /// <remarks>
    /// Answered from published cards, so it costs a cached registry read rather than a negotiation.
    /// A named solver publishes no card and is taken at its word.
    /// </remarks>
    public Task<bool> ServesAsync(long amountSats, CancellationToken cancellationToken = default) =>
        selector.ServesAsync(amountSats, cancellationToken);

    /// <summary>The size range any listed solver serves, for advertising one up front.</summary>
    /// <param name="cancellationToken">Cancels the registry fetch.</param>
    public Task<(long Min, long Max)?> ServedRangeAsync(CancellationToken cancellationToken = default) =>
        selector.ServedRangeAsync(cancellationToken);

    /// <summary>The relay a named solver is reached on, for display. Null when discovered per trade.</summary>
    public string? RelayUri => selector.HasExplicitSolver ? options.RelayUri : null;

    /// <summary>A named solver's public key, for display. Null when discovered per trade.</summary>
    public string? SolverPubkey => selector.HasExplicitSolver ? options.SolverPubkey : null;

    /// <summary>
    /// Run one negotiation against the solver chosen for this trade, then close the transport.
    /// </summary>
    /// <typeparam name="T">What the negotiation produces.</typeparam>
    /// <param name="amountSats">The size being traded, which decides which solver can serve it.</param>
    /// <param name="negotiate">The negotiation to run.</param>
    /// <param name="cancellationToken">Cancels the selection.</param>
    /// <returns>Whatever <paramref name="negotiate"/> returned.</returns>
    /// <exception cref="InvalidOperationException">No solver serves a trade this size.</exception>
    /// <remarks>
    /// <para>
    /// The solver is chosen per trade rather than once at startup, because which one can serve a
    /// swap depends on its size: a size outside one solver's published bounds may be inside
    /// another's, and picking a counterparty before knowing the amount throws that away.
    /// </para>
    /// <para>
    /// <see cref="IRfqTransport"/> is not itself disposable — an HTTP transport holds nothing to
    /// release — so a caller opening one directly has to remember the implementation it got might
    /// be. This closes over that difference rather than leaving each call site to test for it, which
    /// on the Nostr transport is the difference between closing a relay socket and leaking one per
    /// invoice.
    /// </para>
    /// </remarks>
    public async Task<T> WithTransportAsync<T>(
        long amountSats,
        Func<IRfqTransport, Task<T>> negotiate,
        CancellationToken cancellationToken = default)
    {
        var rendezvous = await selector.SelectAsync(amountSats, cancellationToken)
            ?? throw new InvalidOperationException(
                $"No Arkade swap solver serves a Lightning swap of {amountSats} sats. Either no " +
                "solver is listed for this network, or none publishes bounds that include this " +
                "amount. Set solver-relay and solver-pubkey in the Arkade network configuration to " +
                "name one directly.");

        var transport = Open(rendezvous);
        try
        {
            return await negotiate(transport);
        }
        finally
        {
            (transport as IDisposable)?.Dispose();
        }
    }

    /// <summary>Open the transport the rendezvous calls for.</summary>
    /// <remarks>
    /// Chosen by the endpoint's scheme rather than configured separately: an operator who writes an
    /// <c>http://</c> URL has already said which transport they mean, and a second flag that could
    /// disagree with it would only create a way to be wrong. A discovered solver is always reached
    /// over a relay — the registry publishes nothing else.
    /// </remarks>
    private IRfqTransport Open(SolverRendezvous rendezvous) =>
        rendezvous.Relay.Scheme is var scheme
        && (scheme == Uri.UriSchemeHttp || scheme == Uri.UriSchemeHttps)
            ? new HttpRfqTransport(httpClientFactory.CreateClient(), rendezvous.Relay)
            : new NostrRfqTransport(rendezvous.Relay, rendezvous.Pubkey);

    /// <summary>
    /// The key a receive swap seals its preimage to, so somebody can finish the claim.
    /// </summary>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The compressed key, hex.</returns>
    /// <exception cref="InvalidOperationException">A daemon is configured but answered without a key.</exception>
    /// <remarks>
    /// <para>
    /// The corridor's wire schema requires this field, so there is always a key on the wire — but who
    /// holds it is a deployment choice. With a claim daemon configured, the packet is sealed to it and
    /// the swap gains a second party able to claim while we are down. Without one, it is sealed to a
    /// key generated here and immediately discarded: the field is satisfied, the packet is openable by
    /// nobody, and the only claim path is our own.
    /// </para>
    /// <para>
    /// Losing that backstop is a real but bounded cost, and a long-running server is the deployment
    /// that can afford it. Claiming is driven by intent events rather than by a wallet that has to be
    /// open at the right moment. If this server were down for the whole claim window the lockup would
    /// return to the solver and the payer's hold would lapse — a payment that fails, not funds that
    /// are lost.
    /// </para>
    /// <para>
    /// A configured daemon's key is read from it rather than written in configuration: it generates
    /// the key at startup, so a literal copied into a config file goes stale the first time the daemon
    /// restarts and nothing would notice. Cached for this service's lifetime only, for the same
    /// reason. The throwaway key is deliberately not cached — reusing one across swaps would link them
    /// on the wire for no benefit, since neither is openable.
    /// </para>
    /// </remarks>
    public async Task<string> ResolveClaimRecipientAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.CovclaimdUri))
        {
            return new Key().PubKey.Compress().ToHex();
        }

        if (_covclaimdKey is { } cached)
        {
            return cached;
        }

        using var http = httpClientFactory.CreateClient();
        http.BaseAddress = new Uri(options.CovclaimdUri.TrimEnd('/') + "/");

        var doc = await http.GetFromJsonAsync<JsonElement>("v1/preimage/covclaimd-pubkey", cancellationToken);
        if (!doc.TryGetProperty("covclaimd_pub_key", out var key) || key.GetString() is not { Length: > 0 } hex)
        {
            throw new InvalidOperationException(
                $"The claim daemon at {options.CovclaimdUri} answered without a public key.");
        }

        return _covclaimdKey = hex;
    }
}
