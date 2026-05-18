using NArk.Abstractions.Contracts;
using NArk.Core.Contracts;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>
/// Payment prompt details for Ark payments.
/// Stores the contract as a serialized string to avoid JSON converter issues with Network-dependent parsing.
/// </summary>
public record ArkadePromptDetails
{
    /// <summary>
    /// Creates prompt details from a wallet ID and contract.
    /// </summary>
    public ArkadePromptDetails(string walletId, ArkContract contract)
        : this(walletId, contract.ToString())
    {
    }

    /// <summary>
    /// Payment prompt details for Ark payments.
    /// Stores the contract as a serialized string to avoid JSON converter issues with Network-dependent parsing.
    /// </summary>
    public ArkadePromptDetails(string WalletId,
        string ContractString)
    {
        this.WalletId = WalletId;
        this.ContractString = ContractString;
    }
    
    public ArkadePromptDetails()
    {
        
    }

    public string WalletId { get; init; }
    public string ContractString { get; init; }
    public string? BoardingAddress { get; init; }
    public string? BoardingContractString { get; init; }

    // --- Arkade asset acceptance (null unless the store accepts an asset
    // for this payment method; additive, so existing prompts deserialize
    // unchanged). When set, the customer settles by sending this many
    // base units of <see cref="AssetId"/> to the Ark address above.
    public string? AssetId { get; init; }
    public string? AssetName { get; init; }
    public string? AssetTicker { get; init; }
    public int AssetDecimals { get; init; }

    /// <summary>Raw base-unit asset amount the customer must send.</summary>
    public ulong AssetBaseUnitsDue { get; init; }

    /// <summary>Asset amount due, formatted to the asset's decimals.</summary>
    public string? AssetFormattedAmountDue { get; init; }

    /// <summary>
    /// Parses the contract with the specified network.
    /// </summary>
    public ArkContract? GetContract(Network network)
    {
        if (string.IsNullOrEmpty(ContractString))
            return null;
        return ArkContractParser.Parse(ContractString, network);
    }

}
