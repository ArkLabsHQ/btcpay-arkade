using BTCPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class StoreVtxosViewModel : BasePagingViewModel
{
    public IReadOnlyCollection<VTXO> Vtxos { get; set; } = [];
    public HashSet<OutPoint> SpendableOutpoints { get; set; } = [];
    public SearchString Search { get; set; } = new(null);
    public string? SearchText { get; set; }
    public required string StoreId { get; set; }

    public override int CurrentPageCount => Vtxos.Count;
}
