using NArk.ArkadeIntents.Models;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class StoreSwapsViewModel : StoreCollectionViewModelBase
{
    public IReadOnlyCollection<ArkadeSwapIntent> Swaps { get; set; } = [];

    public bool SolverConfigured { get; set; }

    /// <inheritdoc />
    public override int CurrentPageCount => Swaps.Count;
}
