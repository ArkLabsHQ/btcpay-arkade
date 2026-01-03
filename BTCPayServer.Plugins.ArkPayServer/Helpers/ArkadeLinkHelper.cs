using NArk.Abstractions.Contracts;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Helpers;

/// <summary>
/// Helper class for generating Arkade-related external links (indexer, wallet, Boltz, etc.)
/// </summary>
public static class ArkadeLinkHelper
{
    /// <summary>
    /// Generates a link to the Arkade indexer for VTXOs by script.
    /// </summary>
    /// <param name="arkUri">The Arkade operator URI</param>
    /// <param name="contract">The Ark contract</param>
    /// <returns>URL to view VTXOs for this contract in the indexer</returns>
    public static string GetIndexerLinkForContract(string arkUri, ArkContract contract)
    {
        if (string.IsNullOrWhiteSpace(arkUri))
            throw new ArgumentException("Ark URI cannot be null or empty", nameof(arkUri));
        
        if (contract == null)
            throw new ArgumentNullException(nameof(contract));

        var scriptHex = contract.GetArkAddress().ScriptPubKey.ToHex();
        return $"{arkUri.TrimEnd('/')}/v1/indexer/vtxos?scripts={scriptHex}";
    }

    /// <summary>
    /// Generates a link to the Arkade indexer for VTXOs by script hex.
    /// </summary>
    /// <param name="arkUri">The Arkade operator URI</param>
    /// <param name="scriptHex">The script public key in hex format</param>
    /// <returns>URL to view VTXOs for this script in the indexer</returns>
    public static string GetIndexerLinkForScript(string arkUri, string scriptHex)
    {
        if (string.IsNullOrWhiteSpace(arkUri))
            throw new ArgumentException("Ark URI cannot be null or empty", nameof(arkUri));
        
        if (string.IsNullOrWhiteSpace(scriptHex))
            throw new ArgumentException("Script hex cannot be null or empty", nameof(scriptHex));

        return $"{arkUri.TrimEnd('/')}/v1/indexer/vtxos?scripts={scriptHex}";
    }

    /// <summary>
    /// Generates a link to the Arkade indexer for a specific VTXO by outpoint.
    /// </summary>
    /// <param name="arkUri">The Arkade operator URI</param>
    /// <param name="txId">The transaction ID</param>
    /// <param name="outputIndex">The output index</param>
    /// <returns>URL to view this VTXO in the indexer</returns>
    public static string GetIndexerLinkForVtxo(string arkUri, string txId, uint outputIndex)
    {
        if (string.IsNullOrWhiteSpace(arkUri))
            throw new ArgumentException("Ark URI cannot be null or empty", nameof(arkUri));
        
        if (string.IsNullOrWhiteSpace(txId))
            throw new ArgumentException("Transaction ID cannot be null or empty", nameof(txId));

        var outpoint = $"{txId}:{outputIndex}";
        return $"{arkUri.TrimEnd('/')}/v1/indexer/vtxos?outpoints={outpoint}";
    }

    /// <summary>
    /// Generates a link to the Arkade indexer for a specific VTXO by outpoint.
    /// </summary>
    /// <param name="arkUri">The Arkade operator URI</param>
    /// <param name="outpoint">The outpoint</param>
    /// <returns>URL to view this VTXO in the indexer</returns>
    public static string GetIndexerLinkForVtxo(string arkUri, OutPoint outpoint)
    {
        if (string.IsNullOrWhiteSpace(arkUri))
            throw new ArgumentException("Ark URI cannot be null or empty", nameof(arkUri));
        
        if (outpoint == null)
            throw new ArgumentNullException(nameof(outpoint));

        return GetIndexerLinkForVtxo(arkUri, outpoint.Hash.ToString(), outpoint.N);
    }

    /// <summary>
    /// Generates a link to Boltz for a specific swap.
    /// </summary>
    /// <param name="boltzUri">The Boltz API URI</param>
    /// <param name="swapId">The swap ID</param>
    /// <returns>URL to view this swap on Boltz, or null if Boltz URI is not configured</returns>
    public static string? GetBoltzSwapLink(string? boltzUri, string swapId)
    {
        if (string.IsNullOrWhiteSpace(boltzUri))
            return null;
        
        if (string.IsNullOrWhiteSpace(swapId))
            throw new ArgumentException("Swap ID cannot be null or empty", nameof(swapId));

        return $"{boltzUri.TrimEnd('/')}/v2/swap/{swapId}";
    }

    /// <summary>
    /// Generates a link to the Arkade Wallet.
    /// </summary>
    /// <param name="arkadeWalletUri">The Arkade Wallet URI</param>
    /// <returns>URL to the Arkade Wallet, or null if not configured</returns>
    public static string? GetArkadeWalletLink(string? arkadeWalletUri)
    {
        if (string.IsNullOrWhiteSpace(arkadeWalletUri))
            return null;

        return arkadeWalletUri.TrimEnd('/');
    }

    /// <summary>
    /// Generates a formatted outpoint string (txid:index).
    /// </summary>
    /// <param name="txId">The transaction ID</param>
    /// <param name="outputIndex">The output index</param>
    /// <returns>Formatted outpoint string</returns>
    public static string FormatOutpoint(string txId, uint outputIndex)
    {
        if (string.IsNullOrWhiteSpace(txId))
            throw new ArgumentException("Transaction ID cannot be null or empty", nameof(txId));

        return $"{txId}:{outputIndex}";
    }

    /// <summary>
    /// Generates a formatted outpoint string (txid:index).
    /// </summary>
    /// <param name="outpoint">The outpoint</param>
    /// <returns>Formatted outpoint string</returns>
    public static string FormatOutpoint(OutPoint outpoint)
    {
        if (outpoint == null)
            throw new ArgumentNullException(nameof(outpoint));

        return FormatOutpoint(outpoint.Hash.ToString(), outpoint.N);
    }
}
