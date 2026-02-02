using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Hosting;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Helpers;

/// <summary>
/// Helper class for generating Arkade-related external links.
/// Uses explorer when available, falls back to indexer API.
/// </summary>
public static class ArkadeLinkHelper
{

    /// <summary>
    /// Gets a link to Boltz for a specific swap.
    /// Returns null if Boltz URI is not configured.
    /// </summary>
    public static string? GetSwapLink(ArkNetworkConfig config, string swapId)
    {
        return string.IsNullOrWhiteSpace(config.BoltzUri) ? null : $"{config.BoltzUri.TrimEnd('/')}/v2/swap/{swapId}";
    }

    public static string GetTransactionLink(ArkNetworkConfig config, string txId)
    {
        return config.ExplorerUri is not null ? $"{config.ExplorerUri.TrimEnd('/')}/tx/{txId}" : $"{config.ArkUri.TrimEnd('/')}/indexer/v1/tx/{txId}";
    }
    
    public static string GetScriptLink(ArkNetworkConfig config, string script)
    {
        return config.ExplorerUri is not null ? $"{config.ExplorerUri.TrimEnd('/')}/address/{script}" : $"{config.ArkUri.TrimEnd('/')}/v1/indexer/vtxos?scripts={script}";
    }
    
    public static string GetAddressLink(ArkNetworkConfig config, string address)
    {
        return config.ExplorerUri is not null ? $"{config.ExplorerUri.TrimEnd('/')}/address/{address}" : $"{config.ArkUri.TrimEnd('/')}/v1/indexer/vtxos?scripts={ ArkAddress.Parse(address).ScriptPubKey.ToHex()}";
    }
    
    public static string? GetOutpointLink(ArkNetworkConfig config, string outpoint)
    {
        return config.ExplorerUri is not null ? GetTransactionLink(config, OutPoint.Parse(outpoint).Hash.ToString()) : $"{config.ArkUri.TrimEnd('/')}/v1/indexer/vtxos?outpoints={outpoint}";
    } 
    public static string? GetOutpointLink(ArkNetworkConfig config, string txid , uint vout)
    {
        var outpoint = $"{txid}:{vout}";
        return config.ExplorerUri is not null ? GetTransactionLink(config, OutPoint.Parse(outpoint).Hash.ToString()) : $"{config.ArkUri.TrimEnd('/')}/v1/indexer/vtxos?outpoints={outpoint}";
    }
    
    public static string GetContractLink(ArkNetworkConfig config, ArkContract contract)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(contract);

        var scriptHex = contract.GetArkAddress().ScriptPubKey.ToHex();
        return GetScriptLink(config, scriptHex);
    }

    #region Formatting Utilities

    /// <summary>
    /// Generates a formatted outpoint string (txid:index).
    /// </summary>
    public static string FormatOutpoint(string txId, uint outputIndex) => $"{txId}:{outputIndex}";

    /// <summary>
    /// Generates a formatted outpoint string (txid:index).
    /// </summary>
    public static string FormatOutpoint(OutPoint outpoint) => outpoint.ToString();

    /// <summary>
    /// Generates a short version of an outpoint for display (first 8...last 8 chars).
    /// </summary>
    public static string FormatOutpointShort(string outpoint)
    {
        return outpoint.Length > 20 ? $"{outpoint[..8]}...{outpoint[^8..]}" : outpoint;
    }

    #endregion
}
