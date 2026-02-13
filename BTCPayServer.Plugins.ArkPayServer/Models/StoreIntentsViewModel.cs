using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class StoreIntentsViewModel : StoreCollectionViewModelBase
{
    public IReadOnlyCollection<ArkIntent> Intents { get; set; } = [];
    public Dictionary<string, OutPoint[]> IntentVtxoOutpoints { get; set; } = new();
    public Dictionary<OutPoint, ArkVtxo> VtxoLookup { get; set; } = new();

    public override int CurrentPageCount => Intents.Count;
}
