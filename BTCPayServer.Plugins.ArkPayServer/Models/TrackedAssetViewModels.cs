namespace BTCPayServer.Plugins.ArkPayServer.Models;

/// <summary>Result of the add-by-id metadata fetch (indexer prefill).</summary>
public class AssetMetadataResult
{
    public bool Found { get; set; }
    public string AssetId { get; set; } = "";
    public string? Ticker { get; set; }
    public string? Name { get; set; }
    public int Decimals { get; set; }
}

/// <summary>A tracked-asset row for the store-settings list + add/edit form binding.</summary>
public class TrackedAssetRow
{
    public string AssetId { get; set; } = "";
    public string CurrencyCode { get; set; } = "";
    public string? Ticker { get; set; }
    public string? Name { get; set; }
    public int Decimals { get; set; }
    public string RateScript { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
