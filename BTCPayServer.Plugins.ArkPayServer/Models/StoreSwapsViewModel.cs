using NArk.Abstractions.Contracts;
using BTCPayServer.Plugins.ArkPayServer.Data.Legacy;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class StoreSwapsViewModel : StoreCollectionViewModelBase
{
    public IReadOnlyCollection<LegacySwap> Swaps { get; set; } = [];
    public Dictionary<string, ArkContractEntity> SwapContracts { get; set; } = new();
    public bool Debug { get; set; }

    public override int CurrentPageCount => Swaps.Count;
}
