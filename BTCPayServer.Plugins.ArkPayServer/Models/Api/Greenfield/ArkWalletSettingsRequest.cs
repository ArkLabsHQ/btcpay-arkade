namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// Request to update Arkade wallet settings for a store.
/// All fields are optional — only provided fields are updated.
/// </summary>
public class ArkWalletSettingsRequest
{
    /// <summary>
    /// Enable or disable sub-dust amount payments.
    /// Cannot be enabled while auto-sweep destination is configured.
    /// </summary>
    public bool? AllowSubDustAmounts { get; set; }

    /// <summary>
    /// Enable or disable boarding (on-chain to Ark).
    /// </summary>
    public bool? BoardingEnabled { get; set; }

    /// <summary>
    /// Minimum boarding amount in satoshis (must be >= 330).
    /// </summary>
    public long? MinBoardingAmountSats { get; set; }

    /// <summary>
    /// Ark address to use as auto-sweep destination. Set to empty string to clear.
    /// Cannot be set while sub-dust amounts are enabled.
    /// </summary>
    public string? Destination { get; set; }
}
