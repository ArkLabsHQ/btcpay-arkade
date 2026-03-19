using NArk.Abstractions.VTXOs;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

/// <summary>
/// ViewModel for the unified Send Wizard.
/// Supports multiple entry points via query params.
/// </summary>
public class SendWizardViewModel
{
    // Store context
    public string StoreId { get; set; } = "";

    // Query param inputs (for pre-loading)
    public string? VtxoOutpoints { get; set; }
    public string? Destinations { get; set; }
    public string? Destination { get; set; }

    // Hydrated data
    public List<ArkVtxo> AvailableVtxos { get; set; } = new();
    public List<ArkVtxo> SelectedVtxos { get; set; } = new();
    public List<SendOutputViewModel> Outputs { get; set; } = new();

    // Computed state
    public SpendType? DetectedSpendType { get; set; }
    public string CoinSelectionMode { get; set; } = "auto";

    // Balance summary
    public ArkBalancesViewModel? Balances { get; set; }

    // Fee estimation
    public long? EstimatedFeeSats { get; set; }
    public string? FeeDescription { get; set; }

    // Validation
    public List<string> Errors { get; set; } = new();

    // Computed properties
    public long TotalSelectedSats => SelectedVtxos.Sum(v => (long)v.Amount);
    public decimal TotalSelectedBtc => TotalSelectedSats / 100_000_000m;
    public int SelectedCount => SelectedVtxos.Count;
    public bool HasPreselectedCoins => !string.IsNullOrEmpty(VtxoOutpoints);
    public bool HasPrefilledDestination => !string.IsNullOrEmpty(Destinations) || !string.IsNullOrEmpty(Destination);

    // Total available balance
    public long TotalAvailableSats => AvailableVtxos.Sum(v => (long)v.Amount);
    public decimal TotalAvailableBtc => TotalAvailableSats / 100_000_000m;
    public int InstantCoinsCount => AvailableVtxos.Count(v => !v.Swept);
    public int BatchOnlyCoinsCount => AvailableVtxos.Count(v => v.Swept);
}

public class SendOutputViewModel
{
    public string Destination { get; set; } = "";
    public decimal? AmountBtc { get; set; }
    public long? AmountSats => AmountBtc.HasValue ? (long)(AmountBtc.Value * 100_000_000) : null;
    public DestinationType? DetectedType { get; set; }
    public string? Error { get; set; }

    // BIP21 parsed state
    public string? RawBip21 { get; set; }
    public string? ResolvedAddress { get; set; }
    public bool IsReadonly { get; set; }
    public bool IsBip21Parsed { get; set; }

    // Per-output fee breakdown
    public long FeeSats { get; set; }
    public string? FeeDescription { get; set; }
    public bool IsLightning { get; set; }
    public decimal FeePercentage { get; set; }
    public long MinerFeeSats { get; set; }

    // Payout tracking
    public string? PayoutId { get; set; }

    // Display helpers
    public string TypeBadge => DetectedType switch
    {
        DestinationType.ArkAddress => "Arkade",
        DestinationType.BitcoinAddress => "Bitcoin (Batch)",
        DestinationType.LightningInvoice => "Lightning",
        DestinationType.Bip21Uri => "BIP21",
        DestinationType.LnurlPay => "LNURL",
        _ => ""
    };

    public string TypeBadgeClass => DetectedType switch
    {
        DestinationType.ArkAddress => "bg-success",
        DestinationType.BitcoinAddress => "bg-primary",
        DestinationType.LightningInvoice => "text-bg-warning",
        DestinationType.Bip21Uri => "bg-info",
        DestinationType.LnurlPay => "bg-info",
        _ => "bg-secondary"
    };
}

public enum SpendType
{
    Offchain,  // Direct VTXO transfer (Ark to Ark, non-recoverable)
    Batch,     // Join Ark batch (onchain output or recoverable coins)
    Swap       // Lightning swap via Boltz
}

public enum DestinationType
{
    ArkAddress,
    BitcoinAddress,
    LightningInvoice,
    Bip21Uri,
    LnurlPay
}
