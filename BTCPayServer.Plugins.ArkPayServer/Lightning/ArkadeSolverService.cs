using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.SolverRegistry;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>Opens and closes the transport to the solver <see cref="ArkadeSolverSelector"/> picks per trade.</summary>
public class ArkadeSolverService(
    ArkadeSolverOptions options,
    ArkadeSolverSelector selector,
    IHttpClientFactory httpClientFactory,
    ILogger<ArkadeSolverService> logger)
{
    public bool IsConfigured => selector.CanReachASolver;

    // No size check here: which card bound applies depends on trade direction; the quote answers it exactly.
    public Task<bool> HasSolverAsync(CancellationToken cancellationToken = default) =>
        selector.HasLightningSolverAsync(cancellationToken);

    public Task<bool> HasSolverForAsync(
        string quoteCorridor, CancellationToken cancellationToken = default) =>
        selector.HasSolverForAsync(quoteCorridor, cancellationToken);

    public Task<(long Min, long Max)?> ServedRangeAsync(CancellationToken cancellationToken = default) =>
        selector.ServedRangeAsync(cancellationToken);

    public string? RelayUri => selector.HasExplicitSolver ? options.RelayUri : null;

    public string? SolverPubkey => selector.HasExplicitSolver ? options.SolverPubkey : null;

    // IRfqTransport is not IDisposable but the Nostr one is; disposing here avoids leaking a relay socket per invoice.
    // The card is null for a solver named in configuration, which publishes none.
    public Task<T> WithTransportAsync<T>(
        long amountSats,
        Func<IRfqTransport, SolverCard?, Task<T>> negotiate,
        CancellationToken cancellationToken = default,
        bool fallBack = false) =>
        WithTransportAsync(
            amountSats, ArkadeSolverSelector.LightningCorridor, negotiate, cancellationToken, fallBack);

    // Asked for by name: a solver listed for Lightning never agreed to watch an onchain address.
    // fallBack is the caller's to grant, and only a negotiation that commits nothing may: a retry after
    // a lockup was funded would fund a second one.
    public async Task<T> WithTransportAsync<T>(
        long amountSats,
        string quoteCorridor,
        Func<IRfqTransport, SolverCard?, Task<T>> negotiate,
        CancellationToken cancellationToken = default,
        bool fallBack = false)
    {
        var candidates = await selector.CandidatesAsync(amountSats, quoteCorridor, cancellationToken);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No Arkade swap solver is listed for the {quoteCorridor} corridor on this network, " +
                "and none is named in the Arkade network configuration. Set solver-relay and " +
                "solver-pubkey to name one directly.");
        }

        return await NegotiateAcrossAsync(
            fallBack ? candidates : [candidates[0]], negotiate, cancellationToken);
    }

    /// <summary>Tries each way of reaching a solver in turn, and reports the last refusal if none works.</summary>
    public async Task<T> NegotiateAcrossAsync<T>(
        IReadOnlyList<SolverRendezvous> candidates,
        Func<IRfqTransport, SolverCard?, Task<T>> negotiate,
        CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var transport = new FeeCappedRfqTransport(Open(candidates[i]), options.MaxFeeOn);
                return await negotiate(transport, candidates[i].Card);
            }
            catch (Exception e) when (e is not OperationCanceledException && i < candidates.Count - 1)
            {
                logger.LogWarning(e,
                    "Arkade solver at {Relay} did not quote; trying the next of {Count}",
                    candidates[i].Relay, candidates.Count);
            }
        }

        throw new InvalidOperationException("No Arkade swap solver could be reached.");
    }

    // The scheme picks the transport; a discovered solver is always reached over a relay, which is also
    // the only transport that needs the solver's key.
    private IRfqTransport Open(SolverRendezvous rendezvous) =>
        rendezvous.Relay.Scheme is var scheme
        && (scheme == Uri.UriSchemeHttp || scheme == Uri.UriSchemeHttps)
            ? new HttpRfqTransport(httpClientFactory.CreateClient(), rendezvous.Relay)
            : rendezvous.Pubkey is { Length: > 0 } pubkey
                ? new NostrRfqTransport(rendezvous.Relay, pubkey)
                : throw new InvalidOperationException(
                    "A relay needs the solver's key to address it on: set solver-pubkey beside solver-relay.");

    // Null means no claim packet: we claim ourselves, and a throwaway key would only advertise an offline
    // claim path that does not exist. Read live every time, since covclaimd mints its key at startup.
    // An unreachable daemon costs the offline backstop, not the payment.
    public async Task<string?> ResolveClaimRecipientAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.CovclaimdUri))
        {
            return null;
        }

        try
        {
            using var http = httpClientFactory.CreateClient();
            http.BaseAddress = new Uri(options.CovclaimdUri.TrimEnd('/') + "/");

            var doc = await http.GetFromJsonAsync<JsonElement>("v1/preimage/covclaimd-pubkey", cancellationToken);
            if (doc.TryGetProperty("covclaimd_pub_key", out var key) && key.GetString() is { Length: > 0 } hex)
            {
                return hex;
            }

            logger.LogWarning("The claim daemon at {Uri} answered without a public key; swapping without it", options.CovclaimdUri);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "The claim daemon at {Uri} is unreachable; swapping without it", options.CovclaimdUri);
        }

        return null;
    }
}
