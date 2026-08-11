using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NArk.Abstractions;
using LNURL;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

public partial class ArkController
{
    /// <summary>
    /// Send2 - Deprecated. Redirects to unified Send wizard.
    /// </summary>
    [HttpGet("stores/{storeId}/send2")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public IActionResult Send2(
        string storeId,
        string? destinations = null)
    {
        return RedirectToAction(nameof(Send), new { storeId, destinations });
    }

    /// <summary>
    /// Send2 - Add a destination.
    /// </summary>
    [HttpPost("stores/{storeId}/send2/add")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Send2Add(
        string storeId,
        [FromForm] Send2ViewModel model,
        CancellationToken token = default)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig(requireOwnedByStore: false);
        if (errorResult != null)
            return errorResult;

        // Restore state
        var newModel = await BuildSend2ViewModel(storeId, config!.WalletId!, token);
        newModel.MultipleDestinationsMode = model.MultipleDestinationsMode;
        RestoreSend2Destinations(newModel, model.SerializedDestinations);

        // Parse and add new destination
        if (!string.IsNullOrWhiteSpace(model.NewDestination))
        {
            try
            {
                var serverInfo = await clientTransport.GetServerInfoAsync(token);
                var parsed = await ParseSend2DestinationAsync(model.NewDestination.Trim(), model.NewAmountBtc, serverInfo.Network, token);

                if (!parsed.IsValid)
                {
                    newModel.Errors.Add(parsed.Error ?? "Invalid destination");
                }
                else
                {
                    parsed.Index = newModel.Destinations.Count;
                    newModel.Destinations.Add(parsed);

                    // Estimate fees for all destinations
                    await EstimateSend2Fees(newModel, config.WalletId!, token);
                }
            }
            catch (Exception ex)
            {
                newModel.Errors.Add($"Failed to parse destination: {ex.Message}");
            }
        }
        else
        {
            newModel.Errors.Add("Please enter a destination");
        }

        // Preserve user input on errors so the form re-renders with what they typed
        if (newModel.Errors.Any())
        {
            newModel.NewDestination = model.NewDestination;
            newModel.NewAmountBtc = model.NewAmountBtc;
        }

        // Serialize state for next round-trip
        newModel.SerializedDestinations = SerializeSend2Destinations(newModel.Destinations);

        return View("Send2", newModel);
    }

    /// <summary>
    /// Send2 - Remove a destination by index.
    /// </summary>
    [HttpPost("stores/{storeId}/send2/remove")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Send2Remove(
        string storeId,
        [FromForm] Send2ViewModel model,
        [FromForm] int removeIndex,
        CancellationToken token = default)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig(requireOwnedByStore: false);
        if (errorResult != null)
            return errorResult;

        // Restore state
        var newModel = await BuildSend2ViewModel(storeId, config!.WalletId!, token);
        newModel.MultipleDestinationsMode = model.MultipleDestinationsMode;
        RestoreSend2Destinations(newModel, model.SerializedDestinations);

        // Remove destination
        if (removeIndex >= 0 && removeIndex < newModel.Destinations.Count)
        {
            newModel.Destinations.RemoveAt(removeIndex);

            // Re-index remaining destinations
            for (int i = 0; i < newModel.Destinations.Count; i++)
            {
                newModel.Destinations[i].Index = i;
            }

            // Re-estimate fees
            if (newModel.Destinations.Count > 0)
            {
                await EstimateSend2Fees(newModel, config.WalletId!, token);
            }
        }

        // Serialize state
        newModel.SerializedDestinations = SerializeSend2Destinations(newModel.Destinations);

        return View("Send2", newModel);
    }

    /// <summary>
    /// Send2 - Execute the transaction.
    /// </summary>
    [HttpPost("stores/{storeId}/send2/execute")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Send2Execute(
        string storeId,
        [FromForm] Send2ViewModel model,
        CancellationToken token = default)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig(requireOwnedByStore: false);
        if (errorResult != null)
            return errorResult;

        // Restore state
        var newModel = await BuildSend2ViewModel(storeId, config!.WalletId!, token);
        newModel.MultipleDestinationsMode = model.MultipleDestinationsMode;
        RestoreSend2Destinations(newModel, model.SerializedDestinations);

        if (newModel.Destinations.Count == 0)
        {
            newModel.Errors.Add("No destinations to send to");
            return View("Send2", newModel);
        }

        // Re-estimate fees to ensure we have current values
        await EstimateSend2Fees(newModel, config.WalletId!, token);

        // Validate we have enough balance
        if (newModel.RemainingSats < 0)
        {
            newModel.Errors.Add($"Insufficient balance. Need {newModel.GrandTotalSats:#,0} sats, have {newModel.AvailableBalanceSats:#,0} sats");
            newModel.SerializedDestinations = SerializeSend2Destinations(newModel.Destinations);
            return View("Send2", newModel);
        }

        try
        {
            // Check for Lightning destinations
            var lightningDests = newModel.Destinations
                .Where(d => d.Type == Send2DestinationType.LightningInvoice || d.Type == Send2DestinationType.Bip21Lightning)
                .ToList();

            if (lightningDests.Count > 0)
            {
                if (newModel.Destinations.Count > 1)
                {
                    newModel.Errors.Add("Lightning payments can only be sent one at a time");
                    newModel.SerializedDestinations = SerializeSend2Destinations(newModel.Destinations);
                    return View("Send2", newModel);
                }

                // Execute Lightning payment via ArkadeSpendingService
                var lnDest = lightningDests[0];
                var lnDestination = lnDest.ResolvedAddress ?? lnDest.RawDestination;
                await arkadeSpendingService.Spend(store!, lnDestination, token);

                // Mark payout as paid if this was initiated from payout handler
                if (!string.IsNullOrEmpty(lnDest.PayoutId))
                {
                    await MarkPayoutPaid(lnDest.PayoutId, null, token);
                }

                TempData[WellKnownTempData.SuccessMessage] = $"Lightning payment of {lnDest.AmountSats:#,0} sats initiated";
                return RedirectToAction(nameof(StoreOverview), new { storeId });
            }

            // Build Ark outputs
            var outputs = new List<ArkTxOut>();
            foreach (var dest in newModel.Destinations)
            {
                if (dest.Type == Send2DestinationType.ArkAddress || dest.Type == Send2DestinationType.Bip21Ark)
                {
                    var arkAddr = ArkAddress.Parse(dest.ResolvedAddress!);
                    outputs.Add(new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(dest.AmountSats), arkAddr));
                }
                else
                {
                    newModel.Errors.Add($"Unsupported destination type: {dest.Type}");
                    newModel.SerializedDestinations = SerializeSend2Destinations(newModel.Destinations);
                    return View("Send2", newModel);
                }
            }

            // Execute Ark spend (auto coin selection)
            if (outputs.Count > 0)
            {
                var txId = await arkadeSpender.Spend(
                    config.WalletId!,
                    outputs.ToArray(),
                    token);

                // Poll for VTXO updates
                var activeContracts = await contractStorage.GetContracts(walletIds: [config.WalletId!], isActive: true, cancellationToken: token);
                await vtxoSyncService.PollScriptsForVtxos(activeContracts.Select(c => c.Script).ToHashSet(), PostOpVtxoPollSince(), token);

                // Mark payouts as paid if this was initiated from payout handler
                foreach (var dest in newModel.Destinations.Where(d => !string.IsNullOrEmpty(d.PayoutId)))
                {
                    await MarkPayoutPaid(dest.PayoutId!, txId, token);
                }

                TempData[WellKnownTempData.SuccessMessage] = $"Sent {newModel.TotalSendingSats:#,0} sats to {outputs.Count} destination(s). TxId: {txId}";
            }

            return RedirectToAction(nameof(StoreOverview), new { storeId });
        }
        catch (Exception ex)
        {
            newModel.Errors.Add($"Transaction failed: {ex.Message}");
            newModel.SerializedDestinations = SerializeSend2Destinations(newModel.Destinations);
            return View("Send2", newModel);
        }
    }

    private async Task MarkPayoutPaid(string payoutId, uint256? txId, CancellationToken token)
    {
        try
        {
            using var disposable = await arkPayoutHandler.PayoutLocker.LockOrNullAsync(payoutId, 0, token);
            if (disposable is null) return;

            var proof = new ArkPayoutProof
            {
                TransactionId = txId ?? uint256.Zero,
                DetectedInBackground = false
            };
            await pullPaymentHostedService.MarkPaid(new MarkPayoutRequest
            {
                PayoutId = payoutId,
                Proof = arkPayoutHandler.SerializeProof(proof)
            });
        }
        catch
        {
            // Best-effort: if marking fails, background detection will catch it
        }
    }

    private static DestinationType? MapSend2TypeToDestinationType(Send2DestinationType type) => type switch
    {
        Send2DestinationType.ArkAddress => DestinationType.ArkAddress,
        Send2DestinationType.Bip21Ark => DestinationType.Bip21Uri,
        Send2DestinationType.Bip21Lightning => DestinationType.Bip21Uri,
        Send2DestinationType.LightningInvoice => DestinationType.LightningInvoice,
        Send2DestinationType.Lnurl => DestinationType.LnurlPay,
        _ => null
    };

    private async Task<Send2ViewModel> BuildSend2ViewModel(string storeId, string walletId, CancellationToken token)
    {
        // Get spendable offchain coins only (not recoverable, not locked by pending intents)
        var currTime = await bitcoinTimeChainProvider.GetChainTime(token);
        var allCoins = await arkadeSpender.GetAvailableCoins(walletId, token);
        var lockedOutpoints = await intentStorage.GetLockedVtxoOutpoints(walletId, token);
        var lockedSet = new HashSet<NBitcoin.OutPoint>(lockedOutpoints);
        var spendableCoins = allCoins.Where(c => !c.IsRecoverable(currTime) && !lockedSet.Contains(c.Outpoint)).ToList();

        return new Send2ViewModel
        {
            StoreId = storeId,
            AvailableBalanceSats = spendableCoins.Sum(c => c.TxOut.Value.Satoshi),
            SpendableCoinsCount = spendableCoins.Count,
        };
    }

    private async Task<(LNURLPayRequest? info, string? error)> ResolveLnurlAsync(
        string destination, CancellationToken token)
    {
        Uri lnurl;
        if (destination.IsValidEmail())
            lnurl = LNURL.LNURL.ExtractUriFromInternetIdentifier(destination);
        else
            lnurl = LNURL.LNURL.Parse(destination, out _);

        var httpClient = httpClientFactory.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, token);

        var rawInfo = await LNURL.LNURL.FetchInformation(lnurl, httpClient, linked.Token);
        if (rawInfo is not LNURLPayRequest info)
            return (null, "Not a valid LNURL-pay endpoint");

        return (info, null);
    }

    private async Task<(string? bolt11, string? error)> ResolveLnurlToInvoiceAsync(
        string destination, long amountSats, Network network, CancellationToken token)
    {
        var (info, error) = await ResolveLnurlAsync(destination, token);
        if (info == null) return (null, error ?? "LNURL resolution failed");

        var lm = new LightMoney(amountSats, LightMoneyUnit.Satoshi);
        if (lm < info.MinSendable || lm > info.MaxSendable)
            return (null, $"Amount {amountSats} sats outside LNURL range ({info.MinSendable.ToUnit(LightMoneyUnit.Satoshi)}-{info.MaxSendable.ToUnit(LightMoneyUnit.Satoshi)} sats)");

        var httpClient = httpClientFactory.CreateClient();
        var callback = await info.SendRequest(lm, network, httpClient, cancellationToken: token);
        var bolt11 = callback.GetPaymentRequest(network);
        return (bolt11.ToString(), null);
    }

    private async Task<Send2DestinationViewModel> ParseSend2DestinationAsync(
        string rawDestination, decimal? amountBtc, Network network, CancellationToken token)
    {
        // Check if it's an LNURL or Lightning Address FIRST
        if (rawDestination.IsValidEmail() ||
            rawDestination.StartsWith("lnurl", StringComparison.OrdinalIgnoreCase))
        {
            var result = new Send2DestinationViewModel { RawDestination = rawDestination };
            try
            {
                var (info, lnurlError) = await ResolveLnurlAsync(rawDestination, token);
                if (info == null)
                {
                    result.Type = Send2DestinationType.Lnurl;
                    result.Error = lnurlError;
                    return result;
                }

                result.Type = Send2DestinationType.Lnurl;
                result.ResolvedAddress = rawDestination;
                result.LnurlMinSats = (long)info.MinSendable.ToUnit(LightMoneyUnit.Satoshi);
                result.LnurlMaxSats = (long)info.MaxSendable.ToUnit(LightMoneyUnit.Satoshi);

                // Intersect with Boltz submarine swap limits
                if (boltzLimitsValidator != null)
                {
                    var limits = await boltzLimitsValidator.GetAllLimitsAsync(token);
                    if (limits != null)
                    {
                        result.LnurlMinSats = Math.Max(result.LnurlMinSats, limits.SubmarineMinAmount);
                        result.LnurlMaxSats = Math.Min(result.LnurlMaxSats, limits.SubmarineMaxAmount);
                    }
                }

                var amountSats = amountBtc.HasValue ? (long)(amountBtc.Value * 100_000_000m) : 0L;
                result.AmountSats = amountSats;
                result.IsValid = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Type = Send2DestinationType.Lnurl;
                result.Error = $"LNURL resolution failed: {ex.Message}";
                return result;
            }
        }

        // Delegate to existing sync method for all other types
        return ParseSend2Destination(rawDestination, amountBtc, network);
    }

    private static bool IsLightningDestination(string dest) => ArkSpendHelpers.IsLightningDestination(dest);

    private static Send2DestinationViewModel ParseSend2Destination(string rawDestination, decimal? amountBtc, Network network)
    {
        var parsed = ArkSpendHelpers.ParseSendDestination(rawDestination, amountBtc, network);
        return new Send2DestinationViewModel
        {
            RawDestination = parsed.RawDestination,
            Type = parsed.Type,
            ResolvedAddress = parsed.ResolvedAddress,
            AmountSats = parsed.AmountSats,
            PayoutId = parsed.PayoutId,
            LnurlMinSats = parsed.LnurlMinSats,
            LnurlMaxSats = parsed.LnurlMaxSats,
            IsValid = parsed.IsValid,
            Error = parsed.Error
        };
    }

    private async Task EstimateSend2Fees(Send2ViewModel model, string walletId, CancellationToken token)
    {
        var currentTime = await bitcoinTimeChainProvider.GetChainTime(token);
        foreach (var dest in model.Destinations)
        {
            if (!dest.IsValid) continue;

            try
            {
                if (dest.Type == Send2DestinationType.ArkAddress || dest.Type == Send2DestinationType.Bip21Ark)
                {
                    // Ark fee estimation
                    var arkAddr = ArkAddress.Parse(dest.ResolvedAddress!);
                    var outputs = new[] { new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(dest.AmountSats), arkAddr) };

                    var coins = await arkadeSpender.GetAvailableCoins(walletId, token);
                    var feeLockedOps = await intentStorage.GetLockedVtxoOutpoints(walletId, token);
                    var feeLockedSet = new HashSet<NBitcoin.OutPoint>(feeLockedOps);
                    var spendableCoins = coins.Where(c => !c.IsRecoverable(currentTime) && !feeLockedSet.Contains(c.Outpoint)).ToArray();

                    if (spendableCoins.Length > 0)
                    {
                        var fee = await feeEstimator.EstimateFeeAsync(spendableCoins, outputs, token);
                        dest.FeeSats = fee;
                        dest.FeeDescription = "Arkade service fee";
                    }
                }
                else if (dest.Type is Send2DestinationType.LightningInvoice or Send2DestinationType.Bip21Lightning or Send2DestinationType.Lnurl)
                {
                    // Lightning swap fee estimation via Boltz
                    if (boltzLimitsValidator != null)
                    {
                        var limits = await boltzLimitsValidator.GetAllLimitsAsync(token);
                        if (limits != null)
                        {
                            var percentFee = (long)(dest.AmountSats * limits.SubmarineFeePercentage / 100m);
                            var minerFee = limits.SubmarineMinerFee;
                            dest.FeeSats = percentFee + minerFee;
                            dest.FeeDescription = $"Swap fee ({limits.SubmarineFeePercentage:0.##}% + {minerFee:#,0} sat miner)";
                        }
                    }
                }
            }
            catch
            {
                dest.FeeDescription = "Fee estimation unavailable";
            }
        }
    }

    private static string SerializeSend2Destinations(List<Send2DestinationViewModel> destinations)
    {
        // Simple serialization: rawDest|type|resolvedAddr|amountSats|feeSats|isValid|error|payoutId|lnurlMin|lnurlMax;;...
        var parts = destinations.Select(d =>
            $"{d.RawDestination}|{(int)d.Type}|{d.ResolvedAddress ?? ""}|{d.AmountSats}|{d.FeeSats}|{d.IsValid}|{d.Error ?? ""}|{d.PayoutId ?? ""}|{d.LnurlMinSats}|{d.LnurlMaxSats}");
        return string.Join(";;", parts);
    }

    private static void RestoreSend2Destinations(Send2ViewModel model, string? serialized)
    {
        if (string.IsNullOrEmpty(serialized)) return;

        var parts = serialized.Split(";;", StringSplitOptions.RemoveEmptyEntries);
        int index = 0;
        foreach (var part in parts)
        {
            var segments = part.Split('|');
            if (segments.Length >= 6)
            {
                model.Destinations.Add(new Send2DestinationViewModel
                {
                    Index = index++,
                    RawDestination = segments[0],
                    Type = Enum.TryParse<Send2DestinationType>(segments[1], out var t) ? t : Send2DestinationType.Unknown,
                    ResolvedAddress = string.IsNullOrEmpty(segments[2]) ? null : segments[2],
                    AmountSats = long.TryParse(segments[3], out var amt) ? amt : 0,
                    FeeSats = long.TryParse(segments[4], out var fee) ? fee : 0,
                    IsValid = bool.TryParse(segments[5], out var valid) && valid,
                    Error = segments.Length > 6 && !string.IsNullOrEmpty(segments[6]) ? segments[6] : null,
                    PayoutId = segments.Length > 7 && !string.IsNullOrEmpty(segments[7]) ? segments[7] : null,
                    LnurlMinSats = segments.Length > 8 && long.TryParse(segments[8], out var lnMin) ? lnMin : 0,
                    LnurlMaxSats = segments.Length > 9 && long.TryParse(segments[9], out var lnMax) ? lnMax : 0,
                });
            }
        }
    }

    /// <summary>
    /// Parses the destinations query parameter which can contain:
    /// - Full BIP21 URIs (comma-separated, may contain colons in scheme)
    /// - Simple format: addr:amount pairs (comma-separated)
    /// </summary>
    private List<Send2DestinationViewModel> ParseDestinationsParam(string destinations, Network network)
    {
        var result = new List<Send2DestinationViewModel>();

        // Smart split: don't split on commas inside BIP21 URIs
        // BIP21 URIs start with "bitcoin:" and may contain query params with commas
        var parts = new List<string>();
        var currentPart = "";
        var inUri = false;

        foreach (var c in destinations)
        {
            if (c == 'b' && currentPart == "" && destinations.IndexOf("bitcoin:", destinations.IndexOf(c.ToString()), StringComparison.OrdinalIgnoreCase) == destinations.IndexOf(c.ToString()))
            {
                inUri = true;
            }

            if (c == ',' && !inUri)
            {
                if (!string.IsNullOrWhiteSpace(currentPart))
                    parts.Add(currentPart.Trim());
                currentPart = "";
                continue;
            }

            // End of URI detection (space or next bitcoin:)
            if (inUri && (c == ' ' || (c == ',' && currentPart.Contains('?'))))
            {
                if (!string.IsNullOrWhiteSpace(currentPart))
                    parts.Add(currentPart.Trim());
                currentPart = "";
                inUri = c != ',';
                continue;
            }

            currentPart += c;
        }

        if (!string.IsNullOrWhiteSpace(currentPart))
            parts.Add(currentPart.Trim());

        int index = 0;
        foreach (var part in parts)
        {
            // Check if this is a BIP21 URI
            if (part.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = ParseSend2Destination(part, null, network);
                parsed.Index = index++;
                result.Add(parsed);
            }
            // Check if this is a Lightning invoice
            else if (part.StartsWith("ln", StringComparison.OrdinalIgnoreCase) ||
                     part.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = ParseSend2Destination(part, null, network);
                parsed.Index = index++;
                result.Add(parsed);
            }
            // Check if this is an Ark address (no colon, or ark1 prefix)
            else if (part.StartsWith("ark1", StringComparison.OrdinalIgnoreCase) ||
                     ArkAddress.TryParse(part.Split(':')[0], out _))
            {
                // Could be addr:amount format
                var segments = part.Split(':', 2);
                var rawDest = segments[0].Trim();
                decimal? amount = segments.Length > 1 &&
                                  decimal.TryParse(segments[1], System.Globalization.CultureInfo.InvariantCulture, out var amt)
                    ? amt
                    : null;

                var parsed = ParseSend2Destination(rawDest, amount, network);
                parsed.Index = index++;
                result.Add(parsed);
            }
            else
            {
                // Unknown format, try to parse anyway
                var parsed = ParseSend2Destination(part, null, network);
                parsed.Index = index++;
                result.Add(parsed);
            }
        }

        return result;
    }
}
