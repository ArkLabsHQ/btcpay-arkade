using NArk.Abstractions.Contracts;
using NArk.Swaps.Models;

namespace BTCPayServer.Plugins.Arkade.Models;

public class StoreSwapsViewModel : StoreCollectionViewModelBase
{
    public IReadOnlyCollection<ArkSwap> Swaps { get; set; } = [];
    public Dictionary<string, ArkContractEntity> SwapContracts { get; set; } = new();
    public bool Debug { get; set; }
    public HashSet<string> CachedSwapIds { get; set; } = new();

    public override int CurrentPageCount => Swaps.Count;
}
