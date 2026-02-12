using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

/// <summary>
/// View model for the intent/transaction builder page.
/// </summary>
public class IntentBuilderViewModel
{
    /// <summary>
    /// Store ID for form submission.
    /// </summary>
    public string StoreId { get; set; } = "";

    /// <summary>
    /// Whether this is building an intent (offchain) or transaction.
    /// </summary>
    public bool IsIntent { get; set; } = true;

    /// <summary>
    /// Selected VTXOs to spend from.
    /// </summary>
    public List<SelectedVtxoViewModel> SelectedVtxos { get; set; } = [];

    /// <summary>
    /// Total amount available from selected VTXOs (in satoshis).
    /// </summary>
    public long TotalSelectedAmount { get; set; }

    /// <summary>
    /// Outputs/destinations for the spend.
    /// </summary>
    public List<SpendOutputViewModel> Outputs { get; set; } = [new()];

    /// <summary>
    /// Balance information for the wallet.
    /// </summary>
    public ArkBalancesViewModel Balances { get; set; } = new();

    /// <summary>
    /// Comma-separated outpoint strings for form persistence.
    /// </summary>
    public string VtxoOutpointsRaw { get; set; } = "";

    /// <summary>
    /// Whether Lightning is available for single-output payments.
    /// </summary>
    public bool LightningAvailable { get; set; }

    /// <summary>
    /// Validation errors.
    /// </summary>
    public List<string> Errors { get; set; } = [];
}

/// <summary>
/// Represents a selected VTXO for spending.
/// </summary>
public class SelectedVtxoViewModel
{
    /// <summary>
    /// The outpoint string (txid:vout).
    /// </summary>
    public string Outpoint { get; set; } = "";

    /// <summary>
    /// Transaction ID.
    /// </summary>
    public string TransactionId { get; set; } = "";

    /// <summary>
    /// Output index.
    /// </summary>
    public uint OutputIndex { get; set; }

    /// <summary>
    /// Amount in satoshis.
    /// </summary>
    public long Amount { get; set; }

    /// <summary>
    /// Amount formatted in BTC.
    /// </summary>
    public decimal AmountBtc => Money.Satoshis(Amount).ToDecimal(MoneyUnit.BTC);

    /// <summary>
    /// When the VTXO expires.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Whether this VTXO is recoverable (swept).
    /// </summary>
    public bool IsRecoverable { get; set; }

    /// <summary>
    /// Whether this VTXO can be spent offchain.
    /// </summary>
    public bool CanSpendOffchain { get; set; }
}

/// <summary>
/// Represents an output destination for spending.
/// </summary>
public class SpendOutputViewModel
{
    /// <summary>
    /// The destination address, BIP21 URI, or BOLT11 invoice.
    /// </summary>
    public string Destination { get; set; } = "";

    /// <summary>
    /// Amount in BTC (optional - can be parsed from destination).
    /// </summary>
    public decimal? AmountBtc { get; set; }

    /// <summary>
    /// Amount in satoshis (computed from AmountBtc).
    /// </summary>
    public long? AmountSats => AmountBtc.HasValue ? (long)(AmountBtc.Value * 100_000_000m) : null;

    /// <summary>
    /// Output type: Vtxo (offchain) or Onchain.
    /// For Lightning, this is handled separately.
    /// </summary>
    public SpendOutputType OutputType { get; set; } = SpendOutputType.Vtxo;

    /// <summary>
    /// Whether this is a Lightning payment (BOLT11).
    /// </summary>
    public bool IsLightning { get; set; }

    /// <summary>
    /// Validation error for this specific output.
    /// </summary>
    public string? Error { get; set; }
}

/// <summary>
/// Type of output for spending.
/// </summary>
public enum SpendOutputType
{
    /// <summary>
    /// Offchain VTXO output (default).
    /// </summary>
    Vtxo,

    /// <summary>
    /// Onchain Bitcoin output.
    /// </summary>
    Onchain
}

/// <summary>
/// Request for fee estimation.
/// </summary>
public class FeeEstimateRequest
{
    /// <summary>
    /// List of VTXO outpoints being spent (txid:vout format).
    /// </summary>
    public List<string> VtxoOutpoints { get; set; } = [];

    /// <summary>
    /// Total amount of inputs in satoshis.
    /// </summary>
    public long TotalInputSats { get; set; }

    /// <summary>
    /// Output destinations and amounts.
    /// </summary>
    public List<FeeEstimateOutput> Outputs { get; set; } = [];

    /// <summary>
    /// Coin selection mode ("auto" or "manual"). When "auto", server selects coins if none provided.
    /// </summary>
    public string? CoinSelectionMode { get; set; }

    /// <summary>
    /// Spend type preference ("Arkade" or "Batch").
    /// </summary>
    public string? SpendType { get; set; }
}

/// <summary>
/// Output specification for fee estimation.
/// </summary>
public class FeeEstimateOutput
{
    /// <summary>
    /// Destination address or invoice.
    /// </summary>
    public string? Destination { get; set; }

    /// <summary>
    /// Amount in satoshis (optional).
    /// </summary>
    public long? AmountSats { get; set; }
}

/// <summary>
/// Response from fee estimation.
/// </summary>
public class FeeEstimateResponse
{
    /// <summary>
    /// Estimated fee in satoshis.
    /// </summary>
    public long EstimatedFeeSats { get; set; }

    /// <summary>
    /// Human-readable fee description.
    /// </summary>
    public string? FeeDescription { get; set; }

    /// <summary>
    /// Whether this is a Lightning swap (different fee structure).
    /// </summary>
    public bool IsLightning { get; set; }

    /// <summary>
    /// Fee percentage for Lightning swaps.
    /// </summary>
    public decimal FeePercentage { get; set; }

    /// <summary>
    /// Miner fee for Lightning swaps.
    /// </summary>
    public long MinerFeeSats { get; set; }

    /// <summary>
    /// Error message if estimation failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Total input sats from auto-selected coins (returned when server picks coins).
    /// </summary>
    public long TotalInputSats { get; set; }

    /// <summary>
    /// Number of coins selected by the server (auto mode).
    /// </summary>
    public int SelectedCoinCount { get; set; }

    /// <summary>
    /// Outpoints selected by the server (auto mode), so client can sync UI.
    /// </summary>
    public List<string>? SelectedOutpoints { get; set; }
}

/// <summary>
/// Request for server-side destination parsing (AJAX).
/// </summary>
public class ParseDestinationRequest
{
    public string Destination { get; set; } = "";
    public decimal? AmountBtc { get; set; }
}

/// <summary>
/// Response from server-side destination parsing.
/// </summary>
public class ParseDestinationResponse
{
    public string? RawBip21 { get; set; }
    public string? ResolvedAddress { get; set; }
    public string? Type { get; set; }
    public string? TypeBadge { get; set; }
    public string? TypeBadgeClass { get; set; }
    public long AmountSats { get; set; }
    public decimal AmountBtc { get; set; }
    public string? PayoutId { get; set; }
    public bool IsValid { get; set; }
    public string? Error { get; set; }
    public bool IsBip21 { get; set; }
    public bool IsLightning { get; set; }
    public bool IsLnurl { get; set; }
    public long LnurlMinSats { get; set; }
    public long LnurlMaxSats { get; set; }
}
