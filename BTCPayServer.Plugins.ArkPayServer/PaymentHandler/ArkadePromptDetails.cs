using NArk.Contracts;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>
/// Payment prompt details for Ark payments.
/// Stores the contract as a serialized string to avoid JSON converter issues with Network-dependent parsing.
/// </summary>
public record ArkadePromptDetails(
    string WalletId,
    string ContractString)
{
    /// <summary>
    /// Creates prompt details from a wallet ID and contract.
    /// </summary>
    public ArkadePromptDetails(string walletId, ArkContract contract)
        : this(walletId, contract.ToString())
    {
    }

    /// <summary>
    /// Parses the contract with the specified network.
    /// </summary>
    public ArkContract? GetContract(Network network)
    {
        if (string.IsNullOrEmpty(ContractString))
            return null;
        return ArkContract.Parse(ContractString, network);
    }
}
