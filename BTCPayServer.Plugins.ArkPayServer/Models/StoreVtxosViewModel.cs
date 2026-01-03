using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class StoreVtxosViewModel : StoreCollectionViewModelBase
{
    public IReadOnlyCollection<VTXO> Vtxos { get; set; } = [];
    public HashSet<OutPoint> SpendableOutpoints { get; set; } = [];

    // Note: SearchTerm shadows BasePagingViewModel.SearchTerm intentionally
    // to preserve backwards compatibility with existing views
    public new string? SearchTerm { get; set; }

    public override int CurrentPageCount => Vtxos.Count;
}
