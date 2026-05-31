namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// Parsed and classified destination data — mirrors the shape of the MVC parse-destination
/// response used by the Send wizard.
/// </summary>
public class ArkParsedDestinationData
{
    /// <summary>
    /// The raw destination string as supplied by the caller.
    /// </summary>
    public string? RawDestination { get; set; }

    /// <summary>
    /// The destination resolved to its canonical inner form — the Ark address for BIP21 Ark URIs,
    /// the BOLT11 invoice for Lightning, the same email for LNURL-pay addresses, etc.
    /// </summary>
    public string? ResolvedAddress { get; set; }

    /// <summary>
    /// Classification of the destination. One of:
    /// <c>Unknown</c>, <c>ArkAddress</c>, <c>Bip21Ark</c>, <c>Bip21Lightning</c>,
    /// <c>LightningInvoice</c>, <c>Lnurl</c>.
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// Resolved amount in satoshis. Zero when the destination cannot supply an amount
    /// (e.g. bare Ark address with no caller-supplied amount).
    /// </summary>
    public long AmountSats { get; set; }

    /// <summary>
    /// Resolved amount in BTC (derived from <see cref="AmountSats"/>).
    /// </summary>
    public decimal AmountBtc { get; set; }

    /// <summary>
    /// True when <see cref="RawDestination"/> is a parseable BIP21 URI (carrying an Ark address
    /// or Lightning invoice).
    /// </summary>
    public bool IsBip21 { get; set; }

    /// <summary>
    /// True when the destination resolves to any Lightning-shaped target
    /// (BOLT11, BIP21 Lightning, or LNURL).
    /// </summary>
    public bool IsLightning { get; set; }

    /// <summary>
    /// True when the destination is an LNURL-pay endpoint or Lightning Address.
    /// </summary>
    public bool IsLnurl { get; set; }

    /// <summary>
    /// Minimum sendable amount (in sats) advertised by an LNURL endpoint, intersected with
    /// Boltz submarine swap limits when available.
    /// </summary>
    public long LnurlMinSats { get; set; }

    /// <summary>
    /// Maximum sendable amount (in sats) advertised by an LNURL endpoint, intersected with
    /// Boltz submarine swap limits when available.
    /// </summary>
    public long LnurlMaxSats { get; set; }

    /// <summary>
    /// Optional payout ID extracted from a BIP21 URI's <c>payout</c> query parameter.
    /// </summary>
    public string? PayoutId { get; set; }

    /// <summary>
    /// True when the destination is structurally valid and ready to be spent. Note that this
    /// does not guarantee the destination is funded or that the payment will succeed.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Human-readable validation error when <see cref="IsValid"/> is false.
    /// </summary>
    public string? Error { get; set; }
}
