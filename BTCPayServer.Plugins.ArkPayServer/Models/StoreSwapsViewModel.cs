using BTCPayServer.Plugins.ArkPayServer.Data.Entities;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class StoreSwapsViewModel : StoreCollectionViewModelBase
{
    public IReadOnlyCollection<ArkSwap> Swaps { get; set; } = [];
    public bool Debug { get; set; }
    public HashSet<string> CachedSwapIds { get; set; } = new();

    public override int CurrentPageCount => Swaps.Count;
}
