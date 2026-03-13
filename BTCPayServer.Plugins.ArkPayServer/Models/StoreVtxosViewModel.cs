using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class StoreVtxosViewModel : StoreCollectionViewModelBase
{
    public IReadOnlyCollection<ArkVtxo> Vtxos { get; set; } = [];
    public HashSet<OutPoint> SpendableOutpoints { get; set; } = [];

    /// <summary>
    /// Maps VTXO Script to its associated contract info for display
    /// </summary>
    public Dictionary<string, ArkContractEntity> VtxoContracts { get; set; } = new();

    // Note: SearchTerm shadows BasePagingViewModel.SearchTerm intentionally
    // to preserve backwards compatibility with existing views
    public new string? SearchTerm { get; set; }

    public override int CurrentPageCount => Vtxos.Count;
}
