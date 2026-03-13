namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class ArkActivityDashboardWidgetViewModel
{
    public string StoreId { get; set; } = "";
    public List<ActivityItem> Items { get; set; } = [];
}

public class ActivityItem
{
    public DateTimeOffset Date { get; set; }
    public ActivityItemType Type { get; set; }
    public string Label { get; set; } = "";
    public string? StatusText { get; set; }
    public string? StatusClass { get; set; }
    public string? Link { get; set; }
    public string? Amount { get; set; }
}

public enum ActivityItemType
{
    Batch,
    Swap,
    Vtxo
}
