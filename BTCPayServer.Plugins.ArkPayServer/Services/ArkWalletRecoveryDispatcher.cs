using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Core.Recovery;
using NArk.Core.Services;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>Runs idempotent wallet restoration outside an HTTP request scope.</summary>
public sealed class ArkWalletRecoveryDispatcher(IServiceScopeFactory scopes, RecoveryStatusTracker status,
    ILogger<ArkWalletRecoveryDispatcher> logger)
{
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.Ordinal);

    /// <summary>Starts recovery unless this process is already recovering the wallet.</summary>
    public bool Start(string walletId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(walletId);
        if (!_running.TryAdd(walletId, 0)) return false;
        status.SetRunning(walletId);
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var recovery = scope.ServiceProvider.GetService<IWalletRecoveryService>();
                var contractsRecovered = 0;
                var fundsSynced = 0;
                if (recovery is not null)
                {
                    var report = await recovery.RecoverAsync(walletId, cancellationToken: CancellationToken.None);
                    contractsRecovered = report.ContractsRecovered;
                    fundsSynced = report.FundsScriptsSynced;
                }

                var contracts = await scope.ServiceProvider.GetRequiredService<IContractStorage>().GetContracts(
                    walletIds: [walletId], scope: ContractScope.Onchain, cancellationToken: CancellationToken.None);
                if (contracts.Count > 0)
                    await scope.ServiceProvider.GetRequiredService<BoardingUtxoSyncService>()
                        .SyncAsync(contracts, CancellationToken.None);
                status.SetCompleted(walletId, recovery is null ? contracts.Count : contractsRecovered, 0, fundsSynced);
            }
            catch (Exception exception)
            {
                status.SetFailed(walletId, exception.Message);
                logger.LogWarning(exception, "Background wallet recovery failed for wallet {WalletId}", walletId);
            }
            finally
            {
                _running.TryRemove(walletId, out _);
            }
        });
        return true;
    }
}
