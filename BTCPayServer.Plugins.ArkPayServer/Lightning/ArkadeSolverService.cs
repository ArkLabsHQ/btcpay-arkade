using System.Net.Http.Json;
using System.Text.Json;
using NArk.ArkadeIntents.Rfq;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>
/// The solver this deployment trades Lightning corridors with, and how to reach it.
/// </summary>
/// <remarks>
/// <para>
/// One solver, named in configuration. That is a deliberate starting point rather than a limitation
/// worked around: solvers do publish cards to a per-network registry, but an indexed market carries
/// only who trades and what — not the relays to reach them on — so the registry cannot yet open a
/// transport by itself. Naming one solver makes the corridors work today and leaves discovery to be
/// switched on when a card carries enough to dial.
/// </para>
/// <para>
/// The same applies to <see cref="GetCovclaimdKeyAsync"/>, more sharply: the receive corridor's wire
/// schema requires a preimage sealed to a claim daemon, and no card carries that daemon's key.
/// </para>
/// </remarks>
public class ArkadeSolverService(ArkadeSolverOptions options, IHttpClientFactory httpClientFactory)
{
    private string? _covclaimdKey;

    /// <summary>Whether this deployment has a solver to trade with at all.</summary>
    /// <remarks>
    /// Both halves are needed to dial: a relay to connect out to, and the solver's identity to address
    /// on it. Configuring one without the other reaches nobody, so it counts as unconfigured.
    /// </remarks>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.RelayUri)
        && !string.IsNullOrWhiteSpace(options.SolverPubkey);

    /// <summary>Whether this deployment can be paid over Lightning, not merely pay.</summary>
    /// <remarks>
    /// Receiving needs a claim daemon on top of a solver, because the corridor's wire schema requires
    /// a preimage sealed to one and the solver refuses a request without it. Sending needs neither.
    /// </remarks>
    public bool CanReceive => IsConfigured && !string.IsNullOrWhiteSpace(options.CovclaimdUri);

    /// <summary>The relay the solver is reached on, for display.</summary>
    public string? RelayUri => options.RelayUri;

    /// <summary>The solver's x-only public key, hex, for display.</summary>
    public string? SolverPubkey => options.SolverPubkey;

    /// <summary>Open an RFQ transport to the configured solver.</summary>
    /// <returns>A transport the caller owns and must dispose.</returns>
    /// <exception cref="InvalidOperationException">No solver is configured.</exception>
    public IRfqTransport OpenTransport()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "No Arkade swap solver is configured. Set solver-relay and solver-pubkey in the " +
                "Arkade network configuration to enable the Lightning corridors.");
        }

        return new NostrRfqTransport(new Uri(options.RelayUri!), options.SolverPubkey!);
    }

    /// <summary>Run one negotiation against the configured solver, then close the transport.</summary>
    /// <typeparam name="T">What the negotiation produces.</typeparam>
    /// <param name="negotiate">The negotiation to run.</param>
    /// <returns>Whatever <paramref name="negotiate"/> returned.</returns>
    /// <exception cref="InvalidOperationException">No solver is configured.</exception>
    /// <remarks>
    /// <see cref="IRfqTransport"/> is not itself disposable — an HTTP transport holds nothing to
    /// release — so a caller opening one directly has to remember the implementation it got might
    /// be. This closes over that difference rather than leaving each call site to test for it, which
    /// on the Nostr transport is the difference between closing a relay socket and leaking one per
    /// invoice.
    /// </remarks>
    public async Task<T> WithTransportAsync<T>(Func<IRfqTransport, Task<T>> negotiate)
    {
        var transport = OpenTransport();
        try
        {
            return await negotiate(transport);
        }
        finally
        {
            (transport as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The claim daemon's public key, which a receive swap seals its preimage to.
    /// </summary>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The compressed key, hex.</returns>
    /// <exception cref="InvalidOperationException">No daemon is configured, or it answered without a key.</exception>
    /// <remarks>
    /// Read from the daemon rather than configured as a literal: it generates its key at startup, so a
    /// value copied into a config file goes stale the first time the daemon restarts and nothing would
    /// notice — the swap would simply seal to a key nobody holds and lose its offline claim path.
    /// Cached for this service's lifetime only, for the same reason.
    /// </remarks>
    public async Task<string> GetCovclaimdKeyAsync(CancellationToken cancellationToken = default)
    {
        if (_covclaimdKey is { } cached)
        {
            return cached;
        }

        if (string.IsNullOrWhiteSpace(options.CovclaimdUri))
        {
            throw new InvalidOperationException(
                "Receiving over Lightning needs a claim daemon: the corridor seals its preimage to one " +
                "and the solver refuses a request without it. Set covclaimd in the Arkade network " +
                "configuration. Paying invoices does not need it.");
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
