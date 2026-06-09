using System;
using System.Threading;
using System.Threading.Tasks;
using NArk.Core.Transport;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>Cached availability snapshot of the Arkade operator.</summary>
/// <param name="Available">True when the operator answered its last probe.</param>
/// <param name="Error">User-facing reason when unavailable; <c>null</c> when available.</param>
public sealed record ArkOperatorStatus(bool Available, string? Error);

/// <summary>
/// Singleton that tracks whether the Arkade operator (arkd) is reachable, so plugin pages
/// can show a persistent "operator unavailable" banner without each page paying the cost of
/// a fresh probe. A successful <see cref="IClientTransport.GetServerInfoAsync"/> is the
/// liveness signal.
/// <para>
/// The result is cached for <see cref="CacheTtl"/>. We deliberately cache the RESULT, not a
/// <see cref="Task"/> — a cached faulted task would pin the "down" state forever after a
/// single transient hiccup. Because the underlying transport caches a successful
/// server-info for ~5 minutes, a freshly-downed operator can still probe "up" during that
/// window; real plugin operations therefore feed their observed outcome in via
/// <see cref="ReportFailure"/>/<see cref="ReportSuccess"/> so the banner flips immediately on
/// a failed action instead of waiting for the transport cache to expire.
/// </para>
/// </summary>
public sealed class ArkOperatorHealthService(IClientTransport clientTransport)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _probeGate = new(1, 1);

    private volatile ArkOperatorStatus? _cached;
    private long _checkedAtTicks; // DateTimeOffset.UtcNow.UtcTicks — long read/write is atomic.

    /// <summary>
    /// Returns the operator status, re-probing only when the cached value is older than
    /// <see cref="CacheTtl"/>. Never throws — a probe failure becomes an unavailable status.
    /// </summary>
    public async Task<ArkOperatorStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (TryGetFresh(out var fresh))
            return fresh;

        await _probeGate.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have refreshed the cache while we waited on the gate.
            if (TryGetFresh(out fresh))
                return fresh;

            try
            {
                await clientTransport.GetServerInfoAsync(cancellationToken);
                return Store(new ArkOperatorStatus(true, null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller (e.g. the browser) aborted the request — that is not an
                // operator signal, so don't poison the cache with a false "down". Surface
                // the last-known status, defaulting to available when we have none.
                return _cached ?? new ArkOperatorStatus(true, null);
            }
            catch
            {
                // Any other failure means the operator did not answer the probe (gRPC
                // transport fault, HTTP 5xx, the transport's own 10s fetch timeout, …).
                return Store(new ArkOperatorStatus(false, ArkOperatorAvailability.UnavailableMessage));
            }
        }
        finally
        {
            _probeGate.Release();
        }
    }

    /// <summary>
    /// Records a failure observed by a real operation. Flips the cached state to unavailable
    /// (and resets the TTL) only when <paramref name="ex"/> looks like an operator-unreachable
    /// failure — genuine application errors are ignored here.
    /// </summary>
    public void ReportFailure(Exception ex)
    {
        if (ArkOperatorAvailability.IsUnavailable(ex))
            Store(new ArkOperatorStatus(false, ArkOperatorAvailability.UnavailableMessage));
    }

    /// <summary>Records a successful operator interaction, clearing any cached "unavailable" state.</summary>
    public void ReportSuccess() => Store(new ArkOperatorStatus(true, null));

    private bool TryGetFresh(out ArkOperatorStatus status)
    {
        var cached = _cached;
        if (cached is not null &&
            DateTimeOffset.UtcNow.UtcTicks - Interlocked.Read(ref _checkedAtTicks) < CacheTtl.Ticks)
        {
            status = cached;
            return true;
        }

        status = null!;
        return false;
    }

    private ArkOperatorStatus Store(ArkOperatorStatus status)
    {
        _cached = status;
        Interlocked.Exchange(ref _checkedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
        return status;
    }
}
