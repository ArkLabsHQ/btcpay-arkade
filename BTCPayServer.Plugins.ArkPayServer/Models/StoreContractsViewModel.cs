using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Swaps.Models;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class StoreContractsViewModel : StoreCollectionViewModelBase
{
    public IReadOnlyCollection<ArkContractEntity> Contracts { get; set; } = [];
    public Dictionary<string, ArkVtxo[]> ContractVtxos { get; set; } = new();
    public Dictionary<string, ArkSwap[]> ContractSwaps { get; set; } = new();
    public bool CanManageContracts { get; set; }
    public bool Debug { get; set; }
    public HashSet<string> CachedSwapScripts { get; set; } = new();
    public HashSet<string> CachedContractScripts { get; set; } = new();
    public HashSet<string> ListenedScripts { get; set; } = new();

    public override int CurrentPageCount => Contracts.Count;
}