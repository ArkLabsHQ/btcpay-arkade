using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Exceptions;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.Models.Api;
using BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;
using LNURL;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NArk.Abstractions;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

public partial class ArkGreenfieldController
{
    #region Send

    /// <summary>
    /// Send Ark funds to a destination (Ark address, Bitcoin address, Lightning invoice, or BIP21 URI).
    /// </summary>
    [HttpPost("~/api/v1/stores/{storeId}/arkade/send")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> Send(string storeId, [FromBody] ArkSendRequest request,
        CancellationToken cancellationToken)
    {
        var (config, error) = GetStoreConfig();
        if (error != null) return error;

        if (string.IsNullOrWhiteSpace(request.Destination))
            return this.CreateAPIError("missing-destination", "Destination is required.");

        if (!config!.GeneratedByStore)
            return this.CreateAPIError(403, "not-owned", "Wallet is not owned by this store.");

        try
        {
            var store = HttpContext.GetStoreDataOrNull();
            if (store is null)
                return this.CreateAPIError(404, "store-not-found", "Store not found.");

            var txId = await arkadeSpendingService.Spend(
                store,
                request.Destination,
                request.AmountSats,
                request.InputOutpoints,
                cancellationToken);

            return Ok(new ArkSendResponse { TxId = txId });
        }
        catch (MalformedPaymentDestination ex)
        {
            return this.CreateAPIError("invalid-destination", ex.Message);
        }
        catch (IncompleteArkadeSetupException ex)
        {
            return this.CreateAPIError("incomplete-setup", ex.Message);
        }
        catch (ArkadePaymentFailedException ex)
        {
            return this.CreateAPIError("send-failed", ex.Message);
        }
        catch (Exception ex)
        {
            return this.CreateAPIError("send-failed", ex.Message);
        }
    }

    #endregion

    #region Fee estimation

    /// <summary>
    /// Estimate fees for a prospective Arkade send without submitting anything.
    /// Mirrors the data shown in the MVC Send wizard's fee breakdown.
    /// </summary>
    [HttpPost("~/api/v1/stores/{storeId}/arkade/estimate-fees")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> EstimateFees(string storeId, [FromBody] ArkFeeEstimateRequest request,
        CancellationToken cancellationToken)
    {
        var (config, error) = GetStoreConfig();
        if (error != null) return error;

        request ??= new ArkFeeEstimateRequest();
        request.Outputs ??= [];
        request.InputOutpoints ??= [];

        try
        {
            var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
            var response = new ArkFeeEstimateData();

            // Lightning short-circuit: single destination that looks like a Lightning destination.
            if (request.Outputs.Count == 1)
            {
                var dest = request.Outputs[0].Destination?.Trim() ?? string.Empty;
                if (ArkSpendHelpers.IsLightningDestination(dest))
                {
                    if (!arkadeSolver.IsConfigured)
                        return this.CreateAPIError(503, "solver-not-configured",
                            "Lightning swaps are not available: no Arkade swap solver is configured.");

                    var amountSats = request.Outputs[0].AmountSats ?? 0L;
                    if (amountSats <= 0)
                        return this.CreateAPIError("missing-amount",
                            "amountSats is required when estimating a Lightning fee.");

                    // No figure: the fee is the quote's spread, and this call hasn't asked to open a negotiation.
                    response.IsLightning = true;
                    response.FeeDescription = "Swap fee quoted by the solver at send time";
                    return Ok(response);
                }
            }

            // Non-Lightning: resolve coins (auto or explicit), then build outputs and estimate.
            var isAutoMode = string.Equals(request.CoinSelectionMode, "auto", StringComparison.OrdinalIgnoreCase);
            List<ArkCoin> coins;

            if (isAutoMode)
            {
                var allCoins = await arkadeSpender.GetAvailableCoins(config!.WalletId!, cancellationToken);
                var lockedOutpoints = await intentStorage.GetLockedVtxoOutpoints(config.WalletId!, cancellationToken);
                var lockedSet = new HashSet<OutPoint>(lockedOutpoints);
                var availableCoins = allCoins.Where(c => !lockedSet.Contains(c.Outpoint)).ToList();
                if (!availableCoins.Any())
                    return this.CreateAPIError("no-spendable-coins", "No spendable coins available.");

                var (destType, targetSats) = ClassifyEstimateTarget(request);
                var nonRecoverable = availableCoins.Where(c => !c.Swept).ToList();

                SuggestCoinsResponse suggestion = destType switch
                {
                    DestinationType.LightningInvoice =>
                        ArkSpendHelpers.SelectCoins(
                            nonRecoverable.Any() ? nonRecoverable : availableCoins, targetSats, SpendType.Swap),
                    DestinationType.BitcoinAddress =>
                        ArkSpendHelpers.SelectCoins(availableCoins, targetSats, SpendType.Batch),
                    _ when string.Equals(request.SpendType, "Batch", StringComparison.OrdinalIgnoreCase) =>
                        ArkSpendHelpers.SelectCoins(availableCoins, targetSats, SpendType.Batch),
                    _ => nonRecoverable.Any()
                        ? ArkSpendHelpers.SelectCoins(nonRecoverable, targetSats, SpendType.Offchain)
                        : ArkSpendHelpers.SelectCoins(availableCoins, targetSats, SpendType.Batch),
                };

                if (suggestion.Error != null)
                    return this.CreateAPIError("coin-selection-failed", suggestion.Error);

                var selectedSet = suggestion.SuggestedOutpoints.ToHashSet();
                coins = availableCoins
                    .Where(c => selectedSet.Contains(ArkSpendHelpers.FormatOutpoint(c)))
                    .ToList();
                response.SelectedOutpoints = suggestion.SuggestedOutpoints;
            }
            else
            {
                coins = await ResolveCoinsForOutpoints(config!.WalletId!, request.InputOutpoints, cancellationToken);
            }

            if (coins.Count == 0)
                return this.CreateAPIError("no-valid-coins",
                    "No valid coins found for the requested outpoints.");

            response.TotalInputSats = coins.Sum(c => c.TxOut.Value.Satoshi);
            response.SelectedCoinCount = coins.Count;

            // Build outputs.
            var outputs = new List<ArkTxOut>();
            foreach (var outputReq in request.Outputs)
            {
                if (string.IsNullOrWhiteSpace(outputReq.Destination)) continue;

                var (dest, _, outputType) = ArkSpendHelpers.ParseOutputDestination(outputReq.Destination!, serverInfo.Network);
                if (dest == null) continue;

                var amount = outputReq.AmountSats.HasValue
                    ? Money.Satoshis(outputReq.AmountSats.Value)
                    : (request.Outputs.Count == 1 ? Money.Satoshis(response.TotalInputSats) : Money.Zero);

                if (amount > Money.Zero)
                    outputs.Add(new ArkTxOut(outputType, amount, dest));
            }

            // Consolidation placeholder when no explicit outputs.
            if (outputs.Count == 0)
            {
                var totalInput = coins.Sum(c => c.TxOut.Value);
                var placeholderDest = coins.First().Contract.GetArkAddress();
                outputs.Add(new ArkTxOut(ArkTxOutType.Vtxo, totalInput, placeholderDest));
            }

            // Batch with on-chain outputs: include a synthetic change VTXO so the estimator
            // sees the full output set.
            var hasOnchain = outputs.Any(o => o.Type == ArkTxOutType.Onchain);
            var totalOutputSats = outputs.Sum(o => o.Value.Satoshi);
            var totalCoinsSats = coins.Sum(c => c.TxOut.Value.Satoshi);
            if (hasOnchain && totalCoinsSats > totalOutputSats)
            {
                var changePlaceholder = coins.First().Contract.GetArkAddress();
                outputs.Add(new ArkTxOut(
                    ArkTxOutType.Vtxo,
                    Money.Satoshis(totalCoinsSats - totalOutputSats),
                    changePlaceholder));
            }

            // Offchain Arkade-only sends are fee-free.
            if (string.Equals(request.SpendType, "Arkade", StringComparison.OrdinalIgnoreCase) && !hasOnchain)
            {
                response.EstimatedFeeSats = 0;
                response.FeeDescription = "No fee for Arkade transactions";
            }
            else
            {
                response.EstimatedFeeSats = await feeEstimator.EstimateFeeAsync(
                    coins.ToArray(), outputs.ToArray(), cancellationToken);
                response.FeeDescription = hasOnchain ? "Batch transaction fee" : "Ark service fee";
            }

            return Ok(response);
        }
        catch (Exception ex)
        {
            return this.CreateAPIError("fee-estimate-failed", ex.Message);
        }
    }

    private static (DestinationType DestType, long? TargetSats) ClassifyEstimateTarget(ArkFeeEstimateRequest request)
    {
        var destType = DestinationType.ArkAddress;
        long? targetSats = null;

        var firstWithDest = request.Outputs.FirstOrDefault(o => !string.IsNullOrWhiteSpace(o.Destination));
        if (firstWithDest != null)
        {
            var firstDest = firstWithDest.Destination!.Trim();
            if (ArkSpendHelpers.IsLightningDestination(firstDest))
                destType = DestinationType.LightningInvoice;
            else if (firstDest.StartsWith("bc1", StringComparison.OrdinalIgnoreCase)
                  || firstDest.StartsWith("tb1", StringComparison.OrdinalIgnoreCase)
                  || firstDest.StartsWith("bcrt1", StringComparison.OrdinalIgnoreCase)
                  || firstDest.StartsWith("1") || firstDest.StartsWith("3"))
                destType = DestinationType.BitcoinAddress;

            var amounts = request.Outputs.Where(o => o.AmountSats.HasValue).Select(o => o.AmountSats!.Value).ToList();
            if (amounts.Count > 0)
                targetSats = amounts.Sum();
        }

        return (destType, targetSats);
    }

    private async Task<List<ArkCoin>> ResolveCoinsForOutpoints(
        string walletId, IReadOnlyList<string> outpoints, CancellationToken cancellationToken)
    {
        if (outpoints == null || outpoints.Count == 0)
            return [];

        var available = await arkadeSpender.GetAvailableCoins(walletId, cancellationToken);
        var byOutpoint = available.ToDictionary(ArkSpendHelpers.FormatOutpoint);
        var coins = new List<ArkCoin>(outpoints.Count);
        foreach (var raw in outpoints)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (byOutpoint.TryGetValue(raw.Trim(), out var coin))
                coins.Add(coin);
        }
        return coins;
    }

    #endregion

    #region Parse destination

    /// <summary>
    /// Parse and classify a destination string without spending. Mirrors the MVC parse-destination
    /// AJAX endpoint used by the Send wizard for rich destination display.
    /// </summary>
    [HttpPost("~/api/v1/stores/{storeId}/arkade/parse-destination")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> ParseDestination(string storeId,
        [FromBody] ArkParseDestinationRequest request, CancellationToken cancellationToken)
    {
        var (_, error) = GetStoreConfig();
        if (error != null) return error;

        request ??= new ArkParseDestinationRequest();
        if (string.IsNullOrWhiteSpace(request.Destination))
            return this.CreateAPIError("missing-destination", "Destination is required.");

        try
        {
            var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
            var rawDestination = request.Destination.Trim();

            ParsedSendDestination parsed;
            if (rawDestination.IsValidEmail() ||
                rawDestination.StartsWith("lnurl", StringComparison.OrdinalIgnoreCase))
            {
                parsed = await ResolveLnurlDestinationAsync(
                    rawDestination, request.AmountBtc, cancellationToken);
            }
            else
            {
                parsed = ArkSpendHelpers.ParseSendDestination(
                    rawDestination, request.AmountBtc, serverInfo.Network);
            }

            return Ok(MapParsedDestination(parsed));
        }
        catch (Exception ex)
        {
            return Ok(new ArkParsedDestinationData
            {
                RawDestination = request.Destination,
                Type = Send2DestinationType.Unknown.ToString(),
                IsValid = false,
                Error = ex.Message
            });
        }
    }

    private async Task<ParsedSendDestination> ResolveLnurlDestinationAsync(
        string rawDestination, decimal? amountBtc, CancellationToken cancellationToken)
    {
        var result = new ParsedSendDestination
        {
            RawDestination = rawDestination,
            Type = Send2DestinationType.Lnurl
        };

        try
        {
            Uri lnurl = rawDestination.IsValidEmail()
                ? LNURL.LNURL.ExtractUriFromInternetIdentifier(rawDestination)
                : LNURL.LNURL.Parse(rawDestination, out _);

            var httpClient = httpClientFactory.CreateClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);

            var rawInfo = await LNURL.LNURL.FetchInformation(lnurl, httpClient, linked.Token);
            if (rawInfo is not LNURLPayRequest info)
            {
                result.Error = "Not a valid LNURL-pay endpoint";
                return result;
            }

            result.ResolvedAddress = rawDestination;
            result.LnurlMinSats = (long)info.MinSendable.ToUnit(LightMoneyUnit.Satoshi);
            result.LnurlMaxSats = (long)info.MaxSendable.ToUnit(LightMoneyUnit.Satoshi);

            // Unnarrowed: the solver refuses an amount at quoting time, with its reason.

            result.AmountSats = amountBtc.HasValue ? (long)(amountBtc.Value * 100_000_000m) : 0L;
            result.IsValid = true;
            return result;
        }
        catch (Exception ex)
        {
            result.Error = $"LNURL resolution failed: {ex.Message}";
            return result;
        }
    }

    private static ArkParsedDestinationData MapParsedDestination(ParsedSendDestination parsed) =>
        new()
        {
            RawDestination = parsed.RawDestination,
            ResolvedAddress = parsed.ResolvedAddress,
            Type = parsed.Type.ToString(),
            AmountSats = parsed.AmountSats,
            AmountBtc = parsed.AmountSats / 100_000_000m,
            PayoutId = parsed.PayoutId,
            IsValid = parsed.IsValid,
            Error = parsed.Error,
            IsBip21 = parsed.Type is Send2DestinationType.Bip21Ark or Send2DestinationType.Bip21Lightning
                      || (parsed.RawDestination?.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase) ?? false),
            IsLightning = parsed.Type is Send2DestinationType.LightningInvoice
                                       or Send2DestinationType.Bip21Lightning
                                       or Send2DestinationType.Lnurl,
            IsLnurl = parsed.Type == Send2DestinationType.Lnurl,
            LnurlMinSats = parsed.LnurlMinSats,
            LnurlMaxSats = parsed.LnurlMaxSats
        };

    #endregion
}
