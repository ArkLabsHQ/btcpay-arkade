using BTCPayServer.Plugins.Arkade.Data;

namespace BTCPayServer.Plugins.Arkade.Models;

public class StoreIntentsViewModel : StoreCollectionViewModelBase
{
    public IReadOnlyCollection<ArkIntent> Intents { get; set; } = [];
    public Dictionary<string, ArkIntentVtxo[]> IntentVtxos { get; set; } = new();

    public override int CurrentPageCount => Intents.Count;
}
