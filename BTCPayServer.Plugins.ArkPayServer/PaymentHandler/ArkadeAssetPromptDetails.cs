namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>
/// One selectable asset option on an Arkade Asset invoice.
/// </summary>
/// <param name="AssetId">The Arkade asset id (issuance txid + group index).</param>
/// <param name="CurrencyCode">The store currency code the asset is registered under.</param>
/// <param name="Ticker">Display ticker, if known.</param>
/// <param name="Decimals">The asset's declared decimals.</param>
/// <param name="BaseUnitsDue">Raw base-unit amount the payer must send to settle.</param>
/// <param name="FormattedDue">Amount due rendered to the asset's decimals.</param>
/// <param name="Bip321Uri">The asset-only BIP-321 URI (ark + asset + amount).</param>
public record ArkadeAssetOption(
    string AssetId,
    string CurrencyCode,
    string? Ticker,
    int Decimals,
    ulong BaseUnitsDue,
    string FormattedDue,
    string Bip321Uri);

/// <summary>
/// Payment prompt details for the dedicated Arkade Asset payment method: one
/// shared Ark receive address plus one option per enabled tracked asset. The
/// payer picks which asset to send; settlement is detected by which asset
/// actually arrives at <see cref="ArkAddress"/> (matched against the options),
/// so no payer choice is persisted server-side.
/// </summary>
public class ArkadeAssetPromptDetails
{
    public string WalletId { get; set; } = "";
    public string ArkAddress { get; set; } = "";
    public string? ContractString { get; set; }
    public List<ArkadeAssetOption> Options { get; set; } = [];
}
