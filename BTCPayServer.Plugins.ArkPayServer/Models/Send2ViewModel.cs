namespace BTCPayServer.Plugins.ArkPayServer.Models;

/// <summary>
/// Simplified ViewModel for Send2 - no JavaScript, offchain only.
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

    // Coin selection
    public bool ShowCoinSelection { get; set; }
    public List<Send2CoinViewModel> AvailableCoins { get; set; } = new();
    public List<string> SelectedCoinOutpoints { get; set; } = new();
    public string? SerializedSelectedCoins { get; set; }

    // Selected coins balance (when coin selection is active)
    public long SelectedBalanceSats => ShowCoinSelection && SelectedCoinOutpoints.Any()
        ? AvailableCoins.Where(c => SelectedCoinOutpoints.Contains(c.Outpoint)).Sum(c => c.AmountSats)
        : AvailableBalanceSats;
    public decimal SelectedBalanceBtc => SelectedBalanceSats / 100_000_000m;
    public int SelectedCoinsCount => ShowCoinSelection && SelectedCoinOutpoints.Any()
        ? SelectedCoinOutpoints.Count
        : SpendableCoinsCount;

    // Added destinations with calculated fees
    public List<Send2DestinationViewModel> Destinations { get; set; } = new();

    // Computed totals
    public long TotalSendingSats => Destinations.Sum(d => d.AmountSats);
    public decimal TotalSendingBtc => TotalSendingSats / 100_000_000m;
    public long TotalFeesSats => Destinations.Sum(d => d.FeeSats);
    public decimal TotalFeesBtc => TotalFeesSats / 100_000_000m;
    public long GrandTotalSats => TotalSendingSats + TotalFeesSats;
    public decimal GrandTotalBtc => GrandTotalSats / 100_000_000m;

    // Remaining after send (uses selected balance when coin selection is active)
    public long RemainingSats => SelectedBalanceSats - GrandTotalSats;
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

public class Send2CoinViewModel
{
    public string Outpoint { get; set; } = "";
    public long AmountSats { get; set; }
    public decimal AmountBtc => AmountSats / 100_000_000m;
    public bool IsSwept { get; set; }
    public DateTime? CreatedAt { get; set; }

    // Display helpers
    public string ShortOutpoint => Outpoint.Length > 20
        ? $"{Outpoint[..8]}...{Outpoint[^8..]}"
        : Outpoint;
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

    // Validation
    public bool IsValid { get; set; }
    public string? Error { get; set; }

    // Display helpers
    public string TypeBadge => Type switch
    {
        Send2DestinationType.ArkAddress => "Ark",
        Send2DestinationType.Bip21Ark => "BIP21 (Ark)",
        Send2DestinationType.Bip21Lightning => "BIP21 (Lightning)",
        Send2DestinationType.LightningInvoice => "Lightning",
        Send2DestinationType.Lnurl => "LNURL",
        _ => "Unknown"
    };

    public string TypeBadgeClass => Type switch
    {
        Send2DestinationType.ArkAddress => "bg-success",
        Send2DestinationType.Bip21Ark => "bg-success",
        Send2DestinationType.Bip21Lightning => "bg-warning text-dark",
        Send2DestinationType.LightningInvoice => "bg-warning text-dark",
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
