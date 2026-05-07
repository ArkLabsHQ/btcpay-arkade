namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// Intent (pending transaction) details.
/// </summary>
public class ArkIntentData
{
    public string? IntentId { get; set; }
    public string IntentTxId { get; set; } = "";
    public string WalletId { get; set; } = "";
    public string State { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ValidFrom { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }
    public string? BatchId { get; set; }
    public string? CommitmentTransactionId { get; set; }
    public string? CancellationReason { get; set; }

    /// <summary>
    /// VTXO outpoints consumed by this intent.
    /// </summary>
    public List<ArkIntentVtxoData> Vtxos { get; set; } = new();
}

public class ArkIntentVtxoData
{
    public string Outpoint { get; set; } = "";
}
