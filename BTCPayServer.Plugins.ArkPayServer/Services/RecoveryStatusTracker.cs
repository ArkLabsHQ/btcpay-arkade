using System.Collections.Concurrent;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>Lifecycle state of a background wallet-recovery run.</summary>
public enum RecoveryState
{
    Running,
    Completed,
    Failed
}

/// <summary>A per-wallet snapshot of the background recovery job, surfaced on the store overview.</summary>
public record RecoveryStatus(
    RecoveryState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt = null,
    int ContractsRecovered = 0,
    int SwapsAudited = 0,
    int FundsSynced = 0,
    string? Error = null);

/// <summary>
/// In-memory, per-wallet status of the background wallet-recovery job (triggered on
/// wallet import and by the manual "Rescan" action). Process-lifetime only — recovery
/// is idempotent, so a lost status after a restart just means the overview shows
/// "no recent run" until the next Rescan.
/// </summary>
public class RecoveryStatusTracker
{
    private readonly ConcurrentDictionary<string, RecoveryStatus> _byWallet = new();

    public RecoveryStatus? Get(string walletId) =>
        _byWallet.TryGetValue(walletId, out var status) ? status : null;

    public void SetRunning(string walletId) =>
        _byWallet[walletId] = new RecoveryStatus(RecoveryState.Running, DateTimeOffset.UtcNow);

    public void SetCompleted(string walletId, int contractsRecovered, int swapsAudited, int fundsSynced) =>
        _byWallet[walletId] = (_byWallet.TryGetValue(walletId, out var prev)
            ? prev
            : new RecoveryStatus(RecoveryState.Running, DateTimeOffset.UtcNow)) with
        {
            State = RecoveryState.Completed,
            FinishedAt = DateTimeOffset.UtcNow,
            ContractsRecovered = contractsRecovered,
            SwapsAudited = swapsAudited,
            FundsSynced = fundsSynced,
        };

    public void SetFailed(string walletId, string error) =>
        _byWallet[walletId] = (_byWallet.TryGetValue(walletId, out var prev)
            ? prev
            : new RecoveryStatus(RecoveryState.Running, DateTimeOffset.UtcNow)) with
        {
            State = RecoveryState.Failed,
            FinishedAt = DateTimeOffset.UtcNow,
            Error = error,
        };
}
