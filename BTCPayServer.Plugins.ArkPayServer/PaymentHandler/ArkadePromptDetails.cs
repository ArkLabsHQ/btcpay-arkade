using NArk.Abstractions.Contracts;
using NArk.Contracts;
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
