namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>
/// Address(es) for receiving Ark payments.
/// </summary>
public class ArkAddressData
{
    /// <summary>
    /// Ark off-chain address (tark1q... / ark1q...).
    /// </summary>
    public string? Address { get; set; }

    /// <summary>
    /// Boarding address (P2TR onchain) for depositing BTC into Ark.
    /// Only available for HD wallets with boarding enabled.
    /// </summary>
    public string? BoardingAddress { get; set; }
}
