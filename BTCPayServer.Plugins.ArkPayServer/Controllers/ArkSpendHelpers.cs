using System.Globalization;
using BTCPayServer;
using BTCPayServer.Lightning;
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

    /// <summary>
    /// Synchronously parse and classify a destination string in the Send wizard's grammar:
    /// bare Ark address, BOLT11 invoice (with optional <c>lightning:</c> prefix), BIP21 URI
    /// (with <c>ark</c>/<c>lightning</c>/<c>amount</c>/<c>payout</c> query parameters), or
    /// an LNURL / Lightning Address (returned as a placeholder requiring async resolution).
    /// </summary>
    public static ParsedSendDestination ParseSendDestination(
        string rawDestination, decimal? amountBtc, Network network)
    {
        var result = new ParsedSendDestination { RawDestination = rawDestination ?? string.Empty };
        var amountSats = amountBtc.HasValue ? (long)(amountBtc.Value * 100_000_000m) : 0L;

        // Bare Ark address
        if (ArkAddress.TryParse(rawDestination, out var arkAddress) && arkAddress is not null)
        {
            result.Type = Send2DestinationType.ArkAddress;
            result.ResolvedAddress = rawDestination;
            result.AmountSats = amountSats;
            result.IsValid = true;
            if (amountSats <= 0)
                result.Error = "Amount is required for Arkade address";
            return result;
        }

        // BOLT11 (with or without lightning: prefix)
        if (rawDestination.StartsWith("ln", StringComparison.OrdinalIgnoreCase) ||
            rawDestination.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase))
        {
            var invoiceStr = rawDestination.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase)
                ? rawDestination[10..]
                : rawDestination;

            try
            {
                var invoice = BOLT11PaymentRequest.Parse(invoiceStr, network);
                result.Type = Send2DestinationType.LightningInvoice;
                result.ResolvedAddress = invoiceStr;
                result.AmountSats = amountSats > 0
                    ? amountSats
                    : (long)(invoice.MinimumAmount?.ToUnit(LightMoneyUnit.Satoshi) ?? 0);
                result.IsValid = result.AmountSats > 0;
                if (!result.IsValid)
                    result.Error = "Invoice amount could not be determined";
                return result;
            }
            catch
            {
                result.Error = "Invalid Lightning invoice";
                return result;
            }
        }

        // BIP21
        if (Uri.TryCreate(rawDestination, UriKind.Absolute, out var uri) &&
            uri.Scheme.Equals("bitcoin", StringComparison.OrdinalIgnoreCase))
        {
            var host = uri.AbsoluteUri[(uri.Scheme.Length + 1)..].Split('?')[0];
            var qs = uri.ParseQueryString();
            result.PayoutId = qs["payout"];

            if (amountSats == 0 && qs["amount"] is { } amountStr &&
                decimal.TryParse(amountStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var amountDec))
            {
                amountSats = (long)(amountDec * 100_000_000m);
            }

            if (qs["ark"] is { } arkQs && ArkAddress.TryParse(arkQs, out var qsArkAddress) && qsArkAddress is not null)
            {
                result.Type = Send2DestinationType.Bip21Ark;
                result.ResolvedAddress = arkQs;
                result.AmountSats = amountSats;
                result.IsValid = true;
                if (amountSats <= 0)
                    result.Error = "Amount is required";
                return result;
            }

            if (qs["lightning"] is { } lnQs)
            {
                try
                {
                    var invoice = BOLT11PaymentRequest.Parse(lnQs, network);
                    result.Type = Send2DestinationType.Bip21Lightning;
                    result.ResolvedAddress = lnQs;
                    result.AmountSats = amountSats > 0
                        ? amountSats
                        : (long)(invoice.MinimumAmount?.ToUnit(LightMoneyUnit.Satoshi) ?? 0);
                    result.IsValid = result.AmountSats > 0;
                    if (!result.IsValid)
                        result.Error = "Invoice amount could not be determined";
                    return result;
                }
                catch
                {
                    // Fall through to host-as-ark tests
                }
            }

            if (ArkAddress.TryParse(host, out var hostArkAddress) && hostArkAddress is not null)
            {
                result.Type = Send2DestinationType.Bip21Ark;
                result.ResolvedAddress = host;
                result.AmountSats = amountSats;
                result.IsValid = true;
                if (amountSats <= 0)
                    result.Error = "Amount is required";
                return result;
            }

            result.Error =
                "BIP21 URI does not contain an Arkade address or Lightning invoice. Send2 only supports offchain transfers.";
            return result;
        }

        // LNURL / Lightning Address — needs async resolution; the caller decides whether to do it.
        if (rawDestination.StartsWith("lnurl", StringComparison.OrdinalIgnoreCase) ||
            rawDestination.IsValidEmail())
        {
            result.Type = Send2DestinationType.Lnurl;
            result.Error = "LNURL/Lightning Address requires async resolution";
            return result;
        }

        result.Error =
            "Unrecognized destination format. Use an Arkade address, Lightning invoice, or BIP21 URI with ark/lightning parameter.";
        return result;
    }
}

/// <summary>
/// Plain result of <see cref="ArkSpendHelpers.ParseSendDestination"/>, deliberately view-model-agnostic
/// so both MVC and Greenfield code can consume it.
/// </summary>
internal sealed class ParsedSendDestination
{
    public string RawDestination { get; set; } = string.Empty;
    public Send2DestinationType Type { get; set; }
    public string? ResolvedAddress { get; set; }
    public long AmountSats { get; set; }
    public string? PayoutId { get; set; }
    public long LnurlMinSats { get; set; }
    public long LnurlMaxSats { get; set; }
    public bool IsValid { get; set; }
    public string? Error { get; set; }
}
