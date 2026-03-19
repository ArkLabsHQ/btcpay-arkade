namespace BTCPayServer.Plugins.ArkPayServer.Models;

/// <summary>
/// Simplified ViewModel for Send2 - no JavaScript, offchain only, auto coin selection.
/// Form posts add/remove destinations; final submit executes transaction.
/// </summary>
public class Send2ViewModel
{
    public string StoreId { get; set; } = "";

    // Available balance (spendable offchain coins only)
    public long AvailableBalanceSats { get; set; }
    public decimal AvailableBalanceBtc => AvailableBalanceSats / 100_000_000m;
    public int SpendableCoinsCount { get; set; }

    // Current destination input (for adding)
    public string? NewDestination { get; set; }
    public decimal? NewAmountBtc { get; set; }

    // Toggle for multiple destinations mode
    public bool MultipleDestinationsMode { get; set; }

    // Added destinations with calculated fees
    public List<Send2DestinationViewModel> Destinations { get; set; } = new();

    // Computed totals
    public long TotalSendingSats => Destinations.Sum(d => d.AmountSats);
    public decimal TotalSendingBtc => TotalSendingSats / 100_000_000m;
    public long TotalFeesSats => Destinations.Sum(d => d.FeeSats);
    public decimal TotalFeesBtc => TotalFeesSats / 100_000_000m;
    public long GrandTotalSats => TotalSendingSats + TotalFeesSats;
    public decimal GrandTotalBtc => GrandTotalSats / 100_000_000m;

    // Remaining after send
    public long RemainingSats => AvailableBalanceSats - GrandTotalSats;
    public decimal RemainingBtc => RemainingSats / 100_000_000m;

    // Validation
    public List<string> Errors { get; set; } = new();
    public string? SuccessMessage { get; set; }

    // Can execute?
    public bool CanExecute => Destinations.Count > 0
                              && Destinations.All(d => d.IsValid)
                              && RemainingSats >= 0
                              && !Errors.Any();

    // Serialized state for form round-trips
    public string? SerializedDestinations { get; set; }
}

public class Send2DestinationViewModel
{
    public int Index { get; set; }

    // Original input
    public string RawDestination { get; set; } = "";

    // Parsed result
    public Send2DestinationType Type { get; set; }
    public string? ResolvedAddress { get; set; }  // Ark address or parsed from BIP21
    public long AmountSats { get; set; }
    public decimal AmountBtc => AmountSats / 100_000_000m;

    // Fee for this destination
    public long FeeSats { get; set; }
    public decimal FeeBtc => FeeSats / 100_000_000m;
    public string? FeeDescription { get; set; }

    // Payout tracking (when initiated from payout handler)
    public string? PayoutId { get; set; }

    // LNURL metadata (populated on resolution)
    public long LnurlMinSats { get; set; }
    public long LnurlMaxSats { get; set; }

    // Validation
    public bool IsValid { get; set; }
    public string? Error { get; set; }

    // Display helpers
    public string TypeBadge => Type switch
    {
        Send2DestinationType.ArkAddress => "Arkade",
        Send2DestinationType.Bip21Ark => "BIP21 (Arkade)",
        Send2DestinationType.Bip21Lightning => "BIP21 (Lightning)",
        Send2DestinationType.LightningInvoice => "Lightning",
        Send2DestinationType.Lnurl => "LNURL",
        _ => "Unknown"
    };

    public string TypeBadgeClass => Type switch
    {
        Send2DestinationType.ArkAddress => "bg-success",
        Send2DestinationType.Bip21Ark => "bg-success",
        Send2DestinationType.Bip21Lightning => "text-bg-warning",
        Send2DestinationType.LightningInvoice => "text-bg-warning",
        Send2DestinationType.Lnurl => "bg-info",
        _ => "bg-secondary"
    };
}

public enum Send2DestinationType
{
    Unknown,
    ArkAddress,        // Direct ark address (instant, minimal fee)
    Bip21Ark,          // BIP21 with ark= parameter (preferred)
    Bip21Lightning,    // BIP21 with lightning= parameter (swap needed)
    LightningInvoice,  // BOLT11 invoice (swap needed)
    Lnurl              // LNURL-pay (resolve then swap)
}
