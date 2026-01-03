using NArk.Abstractions.VTXOs;
using NArk.Core.Transport;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Simple service for on-demand VTXO polling using NNark's transport and storage.
/// Replaces ArkVtxoSynchronizationService.PollScriptsForVtxos.
/// </summary>
public class VtxoPollingService(IClientTransport clientTransport, IVtxoStorage vtxoStorage)
{
    public async Task PollScriptsForVtxos(IReadOnlySet<string> scripts, CancellationToken cancellationToken = default)
    {
        if (scripts.Count == 0)
            return;

        await foreach (var vtxo in clientTransport.GetVtxoByScriptsAsSnapshot(scripts, cancellationToken))
        {
            await vtxoStorage.UpsertVtxo(vtxo, cancellationToken);
        }
    }
}
