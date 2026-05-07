namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// VTXO (Virtual Transaction Output) details.
/// </summary>
public class ArkVtxoData
{
    public string Outpoint { get; set; } = "";
    public long AmountSats { get; set; }
    public string Script { get; set; } = "";
    public bool IsSpent { get; set; }
    public bool IsSpendable { get; set; }
    public bool IsRecoverable { get; set; }
    public bool IsBoarding { get; set; }
    public string? CommitmentTxId { get; set; }
    public string? ContractType { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Asset details if this VTXO carries an asset (null for plain BTC).
    /// </summary>
    public List<ArkVtxoAssetData>? Assets { get; set; }
}

public class ArkVtxoAssetData
{
    public string AssetId { get; set; } = "";
    public ulong Amount { get; set; }
}
