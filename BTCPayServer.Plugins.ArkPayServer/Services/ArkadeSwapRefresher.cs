using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Services;
using NArk.Core.Services;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public sealed record ArkadeSwapRefreshResult(
    string SwapId,
    ArkadeSwapIntentStatus Before,
    ArkadeSwapIntentStatus After,
    bool Reopened,
    string? Error = null);

/// <summary>Re-reads a swap from the chain and acts on it, the manual counterpart of the advance loop.</summary>
public class ArkadeSwapRefresher(
    VtxoSynchronizationService vtxoSync,
    IArkadeIntentStorage? intentStorage = null,
    ArkadeIntentsService? intents = null)
{
    public async Task<IReadOnlyList<ArkadeSwapRefreshResult>> RefreshAsync(
        string walletId, IReadOnlyCollection<string> swapIds, CancellationToken cancellationToken = default)
    {
        if (intentStorage is null || intents is null)
            throw new InvalidOperationException("The Arkade swap corridors are not configured on this server.");

        var swaps = new List<ArkadeSwapIntent>();
        foreach (var id in swapIds.Distinct())
        {
            // Owned by this wallet or treated as absent, so one store cannot poke another's swaps.
            if (await intentStorage.GetArkadeSwapIntent(id, cancellationToken) is { } swap && swap.WalletId == walletId)
                swaps.Add(swap);
        }

        var reopened = new HashSet<string>();
        foreach (var swap in swaps)
        {
            if (await intents.ReopenAsync(swap.Id, cancellationToken))
                reopened.Add(swap.Id);
        }

        // From the start of history: the lockup may have been funded while nothing was watching it.
        if (swaps.Count > 0)
        {
            await vtxoSync.PollScriptsForVtxos(
                swaps.Select(s => s.SwapPkScript).ToHashSet(), after: null, cancellationToken);
            await intents.ReconcileAsync(walletId, cancellationToken);
        }

        var results = new List<ArkadeSwapRefreshResult>();
        foreach (var swap in swaps)
        {
            string? error = null;
            try
            {
                var advance = await intents.AdvanceAsync(swap.Id, cancellationToken);
                error = advance.Error;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                error = e.Message;
            }

            var after = (await intentStorage.GetArkadeSwapIntent(swap.Id, cancellationToken))?.Status ?? swap.Status;
            results.Add(new ArkadeSwapRefreshResult(swap.Id, swap.Status, after, reopened.Contains(swap.Id), error));
        }

        return results;
    }
}
