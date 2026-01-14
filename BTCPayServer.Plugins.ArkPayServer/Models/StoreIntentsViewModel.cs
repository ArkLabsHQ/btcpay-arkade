using BTCPayServer.Plugins.ArkPayServer.Data;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class StoreIntentsViewModel : StoreCollectionViewModelBase
{
    public IReadOnlyCollection<ArkIntent> Intents { get; set; } = [];
    public Dictionary<int, ArkIntentVtxo[]> IntentVtxos { get; set; } = new();

    public override int CurrentPageCount => Intents.Count;
}
