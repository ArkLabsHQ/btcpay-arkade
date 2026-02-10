using BTCPayServer.Models;

namespace BTCPayServer.Plugins.Arkade.Models;

/// <summary>
/// Base view model for store collection views (Contracts, Swaps, VTXOs, Intents)
/// </summary>
public abstract class StoreCollectionViewModelBase : BasePagingViewModel
{
    public string StoreId { get; set; } = string.Empty;
    public SearchString? Search { get; set; }
    public string? SearchText { get; set; }
}
