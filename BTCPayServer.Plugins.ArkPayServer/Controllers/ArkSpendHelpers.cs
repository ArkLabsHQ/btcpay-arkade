using System.Globalization;
using BTCPayServer;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.Models.Api;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

/// <summary>
/// Stateless helpers for destination parsing and coin selection shared between
/// <see cref="ArkController"/> (MVC) and <see cref="ArkGreenfieldController"/> (Greenfield REST).
/// </summary>
internal static class ArkSpendHelpers
{
    /// <summary>
    /// Returns <c>true</c> if the destination looks like a Lightning destination
    /// (BOLT11, lightning: URI, LNURL, or Lightning Address).
    /// </summary>
    public static bool IsLightningDestination(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return false;
        return destination.StartsWith("ln", StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith("lnurl", StringComparison.OrdinalIgnoreCase)
            || destination.IsValidEmail();
    }

    /// <summary>
    /// Parses a raw destination string into a structured destination, amount, and output type.
    /// Supports: bare Ark address, bare Bitcoin address, BIP21 URI with <c>ark</c>/<c>amount</c>
    /// parameters or Ark/Bitcoin address as host. Returns <c>(null, null, Vtxo)</c> on failure.
    /// </summary>
    public static (IDestination? Destination, Money? Amount, ArkTxOutType OutputType) ParseOutputDestination(
        string rawDestination, Network network)
    {
        var destination = (rawDestination ?? string.Empty).Trim();
        if (destination.Length == 0)
            return (null, null, ArkTxOutType.Vtxo);

        // Try direct Ark address -> VTXO output
        if (ArkAddress.TryParse(destination, out var arkAddress) && arkAddress is not null)
        {
            return (arkAddress, null, ArkTxOutType.Vtxo);
        }

        // Try direct Bitcoin address -> Onchain output
        try
        {
            var btcAddress = BitcoinAddress.Create(destination, network);
            return (btcAddress, null, ArkTxOutType.Onchain);
        }
        catch
        {
            // Not a valid Bitcoin address, continue
        }

        // Try BIP21 URI
        if (Uri.TryCreate(destination, UriKind.Absolute, out var uri) &&
            uri.Scheme.Equals("bitcoin", StringComparison.OrdinalIgnoreCase))
        {
            var host = uri.AbsoluteUri[(uri.Scheme.Length + 1)..].Split('?')[0];
            var qs = uri.ParseQueryString();
            var amount = TryParseBip21Amount(qs["amount"]);

            // Check for ark parameter in query string -> VTXO output
            if (qs["ark"] is { } arkQs && ArkAddress.TryParse(arkQs, out var qsArkAddress) && qsArkAddress is not null)
            {
                return (qsArkAddress, amount, ArkTxOutType.Vtxo);
            }

            // Try host as Ark address -> VTXO output
            if (ArkAddress.TryParse(host, out var hostArkAddress) && hostArkAddress is not null)
            {
                return (hostArkAddress, amount, ArkTxOutType.Vtxo);
            }

            // Try host as Bitcoin address -> Onchain output
            try
            {
                var btcAddress = BitcoinAddress.Create(host, network);
                return (btcAddress, amount, ArkTxOutType.Onchain);
            }
            catch
            {
                // Not a valid Bitcoin address
            }
        }

        return (null, null, ArkTxOutType.Vtxo);
    }

    /// <summary>
    /// Selects coins greedily by descending value to cover <paramref name="targetSats"/>.
    /// When <paramref name="targetSats"/> is null, returns all coins ("send all" mode).
    /// </summary>
    public static SuggestCoinsResponse SelectCoins(
        IReadOnlyList<ArkCoin> coins,
        long? targetSats,
        SpendType spendType)
    {
        if (coins.Count == 0)
        {
            return new SuggestCoinsResponse { Error = "No coins available" };
        }

        var sorted = coins.OrderByDescending(c => c.TxOut.Value.Satoshi).ToList();

        if (!targetSats.HasValue)
        {
            return new SuggestCoinsResponse
            {
                SuggestedOutpoints = sorted.Select(FormatOutpoint).ToList(),
                TotalSats = sorted.Sum(c => c.TxOut.Value.Satoshi),
                SpendType = spendType
            };
        }

        var selected = new List<ArkCoin>();
        long total = 0;

        foreach (var coin in sorted)
        {
            selected.Add(coin);
            total += coin.TxOut.Value.Satoshi;
            if (total >= targetSats.Value)
                break;
        }

        if (total < targetSats.Value)
        {
            return new SuggestCoinsResponse
            {
                Error = $"Insufficient funds. Need {targetSats.Value} sats but only {total} sats available."
            };
        }

        return new SuggestCoinsResponse
        {
            SuggestedOutpoints = selected.Select(FormatOutpoint).ToList(),
            TotalSats = total,
            SpendType = spendType
        };
    }

    /// <summary>
    /// Format an <see cref="ArkCoin"/>'s outpoint as <c>txid:vout</c>.
    /// </summary>
    public static string FormatOutpoint(ArkCoin coin) => $"{coin.Outpoint.Hash}:{coin.Outpoint.N}";

    private static Money? TryParseBip21Amount(string? amountStr)
    {
        if (string.IsNullOrWhiteSpace(amountStr)) return null;
        return decimal.TryParse(amountStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var amountDec)
                && amountDec > 0
            ? Money.Coins(amountDec)
            : null;
    }

}
