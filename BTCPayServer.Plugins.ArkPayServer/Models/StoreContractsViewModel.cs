using BTCPayServer.Plugins.ArkPayServer.Data.Entities;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class StoreContractsViewModel : StoreCollectionViewModelBase
{
    public IReadOnlyCollection<ArkWalletContract> Contracts { get; set; } = [];
    public Dictionary<string, VTXO[]> ContractVtxos { get; set; } = new();
    public Dictionary<string, ArkSwap[]> ContractSwaps { get; set; } = new();
    public bool CanManageContracts { get; set; }
    public bool Debug { get; set; }
    public HashSet<string> CachedSwapScripts { get; set; } = new();
    public HashSet<string> CachedContractScripts { get; set; } = new();

    public override int CurrentPageCount => Contracts.Count;
}