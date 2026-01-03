using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using NArk;
using NArk.Transactions;

namespace BTCPayServer.Plugins.ArkPayServer.Services.Policies;

/// <summary>
/// Interface for policies that determine when VTXOs should trigger scheduled intents
/// (for refreshing expiring VTXOs, moving from recoverable state, etc.)
/// </summary>
public interface IVtxoIntentSchedulingPolicy
{
    /// <summary>
    /// Evaluates if the given spendable coins should trigger a scheduled intent
    /// </summary>
    /// <param name="coins">Available spendable coins</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Intent specification if policy triggered, null otherwise</returns>
    Task<ScheduledIntentSpec?> EvaluateAsync(ArkPsbtSigner[] coins, CancellationToken cancellationToken = default);
}
