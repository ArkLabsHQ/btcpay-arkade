using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Exceptions;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.Models.Api;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Plugins.ArkPayServer.Services.WalletLogger;
using BTCPayServer.Security;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Intents;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Core.Contracts;
using NArk.Hosting;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Swaps.Abstractions;
using NArk.Abstractions.Wallets;
using NArk.Swaps.Models;
using NArk.Storage.EfCore.Entities;
using NArk.Core.Wallet;
using LNURL;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using ArkIntent = NArk.Abstractions.Intents.ArkIntent;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

public partial class ArkController
{
    /// <summary>
    /// Legacy redirect - SpendOverview now redirects to Send wizard.
    /// </summary>
    [HttpGet("stores/{storeId}/spend")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public IActionResult SpendOverview(string storeId, string[]? destinations, string? vtxoOutpoints)
    {
        // Convert old parameters to new format
        var vtxos = vtxoOutpoints;
        var destinationsParam = destinations != null && destinations.Length > 0
            ? string.Join(",", destinations)
            : null;

        return RedirectToAction(nameof(Send), new { storeId, vtxos, destinations = destinationsParam });
    }

    private async Task<IntentBuilderViewModel> BuildIntentBuilderViewModel(
        string storeId,
        string walletId,
        string vtxoOutpointsRaw,
        bool isIntent,
        ArkBalancesViewModel balances,
        CancellationToken token)
    {
        var model = new IntentBuilderViewModel
        {
            StoreId = storeId,
            IsIntent = isIntent,
            VtxoOutpointsRaw = vtxoOutpointsRaw,
            Balances = balances,
            LightningAvailable = true // TODO: Check if Lightning is configured
        };

        // Parse outpoints and load VTXO details
        var outpointStrings = vtxoOutpointsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var parsedOutpoints = ParseOutpoints(outpointStrings);

        var selectedVtxos = await vtxoStorage.GetVtxos(
            outpoints: parsedOutpoints.ToList(),
            walletIds: [walletId],
            includeSpent: true,
            cancellationToken: token);

        foreach (var vtxo in selectedVtxos)
        {
            model.SelectedVtxos.Add(new SelectedVtxoViewModel
            {
                Outpoint = $"{vtxo.TransactionId}:{vtxo.TransactionOutputIndex}",
                TransactionId = vtxo.TransactionId,
                OutputIndex = vtxo.TransactionOutputIndex,
                Amount = (long)vtxo.Amount,
                ExpiresAt = vtxo.ExpiresAt,
                IsRecoverable = vtxo.Swept,
                CanSpendOffchain = !vtxo.IsSpent() && !vtxo.Swept
            });
        }

        model.TotalSelectedAmount = model.SelectedVtxos.Sum(v => v.Amount);

        return model;
    }

    [HttpPost("stores/{storeId}/spend")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SpendOverview(SpendOverviewViewModel model, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(model.Destination))
            return BadRequest();

        var store = HttpContext.GetStoreData();
        if (store == null)
            return NotFound();

        var disposableLock = default(IDisposable);
        try
        {
            var payout = Uri.TryCreate(model.Destination, UriKind.Absolute, out var uriResult)
                ? uriResult.ParseQueryString().Get("payout")
                : null;
            if (!string.IsNullOrEmpty(payout))
            {
                disposableLock = await arkPayoutHandler.PayoutLocker.LockOrNullAsync(payout, 0, token);
                if (disposableLock is null)
                {
                    TempData[WellKnownTempData.ErrorMessage] = "Payment failed: the payout is locked";
                    return RedirectToAction(nameof(SpendOverview),
                        new {storeId = store.Id, destinations = model.PrefilledDestination});

                }
            }

            var maybeProof = await arkadeSpendingService.Spend(store, model.Destination, token);
            //check if destination is a uri and if it has a payout querystring, extract value
            if (!string.IsNullOrEmpty(payout))
            {
                var proof = new ArkPayoutProof()
                {
                    TransactionId = uint256.Parse(maybeProof),
                    DetectedInBackground = false
                };
                var result = await pullPaymentHostedService.MarkPaid(new MarkPayoutRequest()
                {
                    PayoutId = payout,
                    Proof = arkPayoutHandler.SerializeProof(proof)
                });

                TempData[WellKnownTempData.SuccessMessage] =
                    $"Payment sent to {model.Destination} with payout {payout} result {result}";
            }
            else
            {

                TempData[WellKnownTempData.SuccessMessage] = $"Payment sent to {model.Destination}";
            }

            model.PrefilledDestination.Remove(model.Destination);
            return RedirectToAction(nameof(SpendOverview),
                new {storeId = store.Id, destinations = model.PrefilledDestination});
        }
        catch (IncompleteArkadeSetupException e)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Payment failed: incomplete arkade setup!";
            return RedirectToAction(nameof(InitialSetup), new {storeId = store.Id});
        }
        catch (MalformedPaymentDestination e)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Payment failed: malfomed destination!";
            return RedirectToAction(nameof(SpendOverview),
                new {storeId = store.Id, destinations = model.PrefilledDestination});
        }
        catch (ArkadePaymentFailedException e)
        {
            TempData[WellKnownTempData.ErrorMessage] = DescribeArkError(e, "Payment failed: reason");
            return RedirectToAction(nameof(SpendOverview),
                new {storeId = store.Id, destinations = model.PrefilledDestination});
        }
        finally
        {
            if(disposableLock is not null)
            {
                disposableLock.Dispose();
            }
        }
    }

    [HttpPost("stores/{storeId}/build-intent")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> BuildIntent(string storeId, IntentBuilderViewModel model, CancellationToken token)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig(requireOwnedByStore: true);
        if (errorResult != null) return errorResult;

        // Get the selected coins
        var outpointStrings = model.VtxoOutpointsRaw?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var selectedCoins = await GetCoinsForOutpoints(config!.WalletId!, outpointStrings.ToList(), token);

        if (selectedCoins.Count == 0)
        {
            model.Errors.Add("No valid VTXOs selected.");
            model.Balances = await GetArkBalances(config.WalletId!, token);
            await ReloadSelectedVtxos(model, config.WalletId!, token);
            return View("IntentBuilder", model);
        }

        var totalInputAmount = selectedCoins.Sum(c => c.TxOut.Value.Satoshi);

        // Get valid outputs (non-empty destinations)
        var validOutputs = model.Outputs.Where(o => !string.IsNullOrWhiteSpace(o.Destination)).ToList();

        // Check for Lightning - only single output allowed
        var lightningOutputs = validOutputs.Where(o =>
            o.Destination.StartsWith("ln", StringComparison.OrdinalIgnoreCase) ||
            o.Destination.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase)).ToList();

        if (lightningOutputs.Any() && validOutputs.Count > 1)
        {
            model.Errors.Add("Lightning payments only support a single output.");
            model.Balances = await GetArkBalances(config!.WalletId!, token);
            await ReloadSelectedVtxos(model, config.WalletId!, token);
            return View("IntentBuilder", model);
        }

        try
        {
            // If single Lightning output, use existing spend flow
            if (lightningOutputs.Count == 1)
            {
                var lnDestination = lightningOutputs[0].Destination
                    .Replace("lightning:", "", StringComparison.OrdinalIgnoreCase);
                await arkadeSpendingService.Spend(store!, lnDestination, token);
                TempData[WellKnownTempData.SuccessMessage] = "Lightning payment initiated successfully.";
                return RedirectToAction(nameof(Vtxos), new { storeId });
            }

            // Build ArkTxOut array from outputs
            var serverInfo = await clientTransport.GetServerInfoAsync(token);
            var arkOutputs = new List<ArkTxOut>();

            foreach (var output in validOutputs)
            {
                var parseResult = ParseOutputDestination(output, serverInfo.Network);
                if (parseResult.Destination == null)
                {
                    output.Error = "Invalid destination address.";
                    model.Errors.Add($"Invalid destination: {output.Destination}");
                    continue;
                }

                // Amount priority: destination-specified > user-specified > (single output: send all)
                var outputAmount = parseResult.Amount ?? (output.AmountBtc.HasValue ? Money.Coins(output.AmountBtc.Value) : null);

                if (outputAmount == null || outputAmount == Money.Zero)
                {
                    if (validOutputs.Count == 1)
                    {
                        // Single output with no amount specified anywhere - send all
                        outputAmount = Money.Satoshis(totalInputAmount);
                    }
                    else
                    {
                        // Multi-output requires explicit amount
                        output.Error = "Amount is required.";
                        model.Errors.Add($"Amount is required for output: {output.Destination}");
                        continue;
                    }
                }

                // Output type is determined by address type:
                // - Ark address = VTXO (offchain)
                // - Bitcoin address = Onchain
                arkOutputs.Add(new ArkTxOut(parseResult.OutputType, outputAmount, parseResult.Destination));
            }

            if (model.Errors.Any())
            {
                model.Balances = await GetArkBalances(config.WalletId!, token);
                await ReloadSelectedVtxos(model, config.WalletId!, token);
                return View("IntentBuilder", model);
            }

            // Execute the spend with selected coins
            // If no outputs specified, SpendingService will send everything as change to self
            var txId = await arkadeSpender.Spend(config.WalletId!, selectedCoins.ToArray(), arkOutputs.ToArray(), token);

            // Poll for VTXO updates
            var activeContracts = await contractStorage.GetContracts(walletIds: [config.WalletId!], isActive: true, cancellationToken: token);
            await vtxoSyncService.PollScriptsForVtxos(activeContracts.Select(c => c.Script).ToHashSet(), PostOpVtxoPollSince(), token);

            TempData[WellKnownTempData.SuccessMessage] = $"Successfully joined batch. Your VTXOs will be updated in the next round. Transaction ID: {txId}";

            return RedirectToAction(nameof(StoreOverview), new { storeId });
        }
        catch (Exception ex)
        {
            model.Errors.Add($"Failed to build: {ex.Message}");
            model.Balances = await GetArkBalances(config!.WalletId!, token);
            await ReloadSelectedVtxos(model, config.WalletId!, token);
            return View("IntentBuilder", model);
        }
    }

    private static (IDestination? Destination, Money? Amount, ArkTxOutType OutputType) ParseOutputDestination(SpendOutputViewModel output, Network network)
        => ArkSpendHelpers.ParseOutputDestination(output.Destination, network);

    private async Task ReloadSelectedVtxos(IntentBuilderViewModel model, string walletId, CancellationToken token)
    {
        model.SelectedVtxos.Clear();
        if (string.IsNullOrEmpty(model.VtxoOutpointsRaw)) return;

        var outpointStrings = model.VtxoOutpointsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var parsedOutpoints = ParseOutpoints(outpointStrings);

        var selectedVtxos = await vtxoStorage.GetVtxos(
            outpoints: parsedOutpoints.ToList(),
            walletIds: [walletId],
            includeSpent: true,
            cancellationToken: token);

        foreach (var vtxo in selectedVtxos)
        {
            model.SelectedVtxos.Add(new SelectedVtxoViewModel
            {
                Outpoint = $"{vtxo.TransactionId}:{vtxo.TransactionOutputIndex}",
                TransactionId = vtxo.TransactionId,
                OutputIndex = vtxo.TransactionOutputIndex,
                Amount = (long)vtxo.Amount,
                ExpiresAt = vtxo.ExpiresAt,
                IsRecoverable = vtxo.Swept,
                CanSpendOffchain = !vtxo.IsSpent() && !vtxo.Swept
            });
        }

        model.TotalSelectedAmount = model.SelectedVtxos.Sum(v => v.Amount);
    }

    [HttpPost("stores/{storeId}/estimate-fees")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> EstimateFees(string storeId, [FromBody] FeeEstimateRequest request, CancellationToken token)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig(requireOwnedByStore: true);
        if (errorResult != null) return BadRequest("Invalid store configuration");

        try
        {
            var serverInfo = await clientTransport.GetServerInfoAsync(token);
            var response = new FeeEstimateResponse();

            // Check if this is a Lightning payment
            if (request.Outputs.Count == 1)
            {
                var dest = request.Outputs[0].Destination?.Trim() ?? "";
                if (IsLightningDestination(dest))
                {
                    // Lightning swap fees
                    if (boltzLimitsValidator != null)
                    {
                        var limits = await boltzLimitsValidator.GetAllLimitsAsync(token);
                        if (limits != null)
                        {
                            var amount = request.Outputs[0].AmountSats ?? request.TotalInputSats;

                            response.IsLightning = true;
                            response.FeePercentage = limits.SubmarineFeePercentage * 100; // Convert to percentage for display
                            response.MinerFeeSats = limits.SubmarineMinerFee;
                            response.EstimatedFeeSats = (long)Math.Ceiling(amount * limits.SubmarineFeePercentage) + limits.SubmarineMinerFee;
                            response.FeeDescription = $"{limits.SubmarineFeePercentage * 100:F2}% + {limits.SubmarineMinerFee} sats miner fee";
                        }
                        else
                        {
                            response.Error = "Failed to fetch Boltz limits";
                        }
                    }
                    else
                    {
                        response.Error = "Lightning swaps not available";
                    }

                    return Json(response);
                }
            }

            // Ark intent/transaction fees - need to get coins and build outputs
            var isAutoMode = string.Equals(request.CoinSelectionMode, "auto", StringComparison.OrdinalIgnoreCase);
            List<ArkCoin> coins;

            if (isAutoMode)
            {
                // Auto mode: use smart coin selection based on destination type
                var allCoins = await arkadeSpender.GetAvailableCoins(config!.WalletId!, token);
                var lockedOutpoints = await intentStorage.GetLockedVtxoOutpoints(config.WalletId!, token);
                var lockedSet = new HashSet<NBitcoin.OutPoint>(lockedOutpoints);
                var availableCoins = allCoins.Where(c => !lockedSet.Contains(c.Outpoint)).ToList();

                if (!availableCoins.Any())
                {
                    response.Error = "No spendable coins available";
                    return Json(response);
                }

                // Determine destination type for smart selection
                var destType = DestinationType.ArkAddress; // default: consolidation / ark send
                long? targetSats = null;

                if (request.Outputs.Any(o => !string.IsNullOrWhiteSpace(o.Destination)))
                {
                    var firstDest = request.Outputs.First(o => !string.IsNullOrWhiteSpace(o.Destination)).Destination!.Trim();
                    if (IsLightningDestination(firstDest))
                        destType = DestinationType.LightningInvoice;
                    else if (firstDest.StartsWith("bc1", StringComparison.OrdinalIgnoreCase)
                          || firstDest.StartsWith("tb1", StringComparison.OrdinalIgnoreCase)
                          || firstDest.StartsWith("bcrt1", StringComparison.OrdinalIgnoreCase)
                          || firstDest.StartsWith("1") || firstDest.StartsWith("3"))
                        destType = DestinationType.BitcoinAddress;

                    // Calculate target amount
                    var amounts = request.Outputs.Where(o => o.AmountSats.HasValue).Select(o => o.AmountSats!.Value).ToList();
                    if (amounts.Any())
                        targetSats = amounts.Sum();
                }

                // Reuse the same selection logic as SuggestCoins
                var nonRecoverable = availableCoins.Where(c => !c.Swept).ToList();
                var recoverable = availableCoins.Where(c => c.Swept).ToList();
                SuggestCoinsResponse suggestion;

                if (destType == DestinationType.LightningInvoice)
                {
                    suggestion = SelectCoins(nonRecoverable.Any() ? nonRecoverable : availableCoins, targetSats, SpendType.Swap);
                }
                else if (destType == DestinationType.BitcoinAddress)
                {
                    suggestion = SelectCoins(availableCoins, targetSats, SpendType.Batch);
                }
                else if (string.Equals(request.SpendType, "Batch", StringComparison.OrdinalIgnoreCase))
                {
                    suggestion = SelectCoins(availableCoins, targetSats, SpendType.Batch);
                }
                else
                {
                    // Ark address / offchain: prefer non-recoverable
                    suggestion = nonRecoverable.Any()
                        ? SelectCoins(nonRecoverable, targetSats, SpendType.Offchain)
                        : SelectCoins(availableCoins, targetSats, SpendType.Batch);
                }

                if (suggestion.Error != null)
                {
                    response.Error = suggestion.Error;
                    return Json(response);
                }

                // Map selected outpoints back to coins
                var selectedSet = suggestion.SuggestedOutpoints.ToHashSet();
                coins = availableCoins.Where(c => selectedSet.Contains($"{c.Outpoint.Hash}:{c.Outpoint.N}")).ToList();

                // Populate response with selected coin info
                response.TotalInputSats = coins.Sum(c => c.TxOut.Value.Satoshi);
                response.SelectedCoinCount = coins.Count;
                response.SelectedOutpoints = suggestion.SuggestedOutpoints;

                request.TotalInputSats = response.TotalInputSats;
            }
            else
            {
                coins = await GetCoinsForOutpoints(config!.WalletId!, request.VtxoOutpoints, token);
            }

            if (coins.Count == 0)
            {
                response.Error = "No valid coins found for selected outpoints";
                return Json(response);
            }

            var outputs = new List<ArkTxOut>();
            foreach (var outputReq in request.Outputs)
            {
                if (string.IsNullOrWhiteSpace(outputReq.Destination)) continue;

                var parseResult = ParseOutputDestination(new SpendOutputViewModel { Destination = outputReq.Destination }, serverInfo.Network);
                if (parseResult.Destination == null) continue;

                var amount = outputReq.AmountSats.HasValue
                    ? Money.Satoshis(outputReq.AmountSats.Value)
                    : (request.Outputs.Count == 1 ? Money.Satoshis(request.TotalInputSats) : Money.Zero);

                if (amount > Money.Zero)
                {
                    outputs.Add(new ArkTxOut(parseResult.OutputType, amount, parseResult.Destination));
                }
            }

            // If no outputs specified, this is a consolidation (send to self)
            // For fee estimation, we use a placeholder - fee is based on input/output amounts and types
            if (outputs.Count == 0)
            {
                var totalInput = coins.Sum(c => c.TxOut.Value);
                // Use first coin's contract address as placeholder for fee estimation
                // The actual destination will be derived at spend time
                var placeholderDest = coins.First().Contract.GetArkAddress();
                outputs.Add(new ArkTxOut(ArkTxOutType.Vtxo, totalInput, placeholderDest));
            }

            // For batch with on-chain outputs, include a change VTXO output for accurate fee estimation
            var hasOnchain = outputs.Any(o => o.Type == ArkTxOutType.Onchain);
            var totalOutputSats = outputs.Sum(o => o.Value.Satoshi);
            var totalCoinsSats = coins.Sum(c => c.TxOut.Value.Satoshi);
            if (hasOnchain && totalCoinsSats > totalOutputSats)
            {
                var changePlaceholder = coins.First().Contract.GetArkAddress();
                outputs.Add(new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(totalCoinsSats - totalOutputSats), changePlaceholder));
            }

            // Estimate the fee — Arkade (offchain) sends have no fee, only Batch intents do
            if (string.Equals(request.SpendType, "Arkade", StringComparison.OrdinalIgnoreCase) && !hasOnchain)
            {
                response.EstimatedFeeSats = 0;
                response.FeeDescription = "No fee for Arkade transactions";
            }
            else
            {
                var estimatedFee = await feeEstimator.EstimateFeeAsync(coins.ToArray(), outputs.ToArray(), token);
                response.EstimatedFeeSats = estimatedFee;
                response.FeeDescription = hasOnchain ? "Batch transaction fee" : "Arkade service fee";
            }

            return Json(response);
        }
        catch (Exception ex)
        {
            return Json(new FeeEstimateResponse { Error = ex.Message });
        }
    }

    /// <summary>
    /// Parse a destination string server-side (BIP21, Lightning, Ark address).
    /// Used by Send wizard AJAX for rich destination display.
    /// </summary>
    [HttpPost("stores/{storeId}/parse-destination")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ParseDestination(
        string storeId,
        [FromBody] ParseDestinationRequest request,
        CancellationToken token)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig(requireOwnedByStore: true);
        if (errorResult != null) return BadRequest("Invalid store configuration");

        try
        {
            var serverInfo = await clientTransport.GetServerInfoAsync(token);
            var parsed = await ParseSend2DestinationAsync(request.Destination, request.AmountBtc, serverInfo.Network, token);

            return Json(new ParseDestinationResponse
            {
                RawBip21 = parsed.RawDestination,
                ResolvedAddress = parsed.ResolvedAddress,
                Type = parsed.Type.ToString(),
                TypeBadge = parsed.TypeBadge,
                TypeBadgeClass = parsed.TypeBadgeClass,
                AmountSats = parsed.AmountSats,
                AmountBtc = parsed.AmountBtc,
                PayoutId = parsed.PayoutId,
                IsValid = parsed.IsValid,
                Error = parsed.Error,
                IsBip21 = parsed.Type is Send2DestinationType.Bip21Ark or Send2DestinationType.Bip21Lightning
                          || parsed.RawDestination.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase),
                IsLightning = parsed.Type is Send2DestinationType.LightningInvoice or Send2DestinationType.Bip21Lightning
                              or Send2DestinationType.Lnurl,
                IsLnurl = parsed.Type == Send2DestinationType.Lnurl,
                LnurlMinSats = parsed.LnurlMinSats,
                LnurlMaxSats = parsed.LnurlMaxSats,
            });
        }
        catch (Exception ex)
        {
            return Json(new ParseDestinationResponse { Error = ex.Message });
        }
    }

    /// <summary>
    /// Suggests optimal coin selection based on destination type and amount.
    /// </summary>
    [HttpPost("stores/{storeId}/suggest-coins")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SuggestCoins(
        string storeId,
        [FromBody] SuggestCoinsRequest request,
        CancellationToken token)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig(requireOwnedByStore: false);
        if (errorResult != null)
            return Json(new SuggestCoinsResponse { Error = "Store not configured" });

        try
        {
            var allCoins = await arkadeSpender.GetAvailableCoins(config!.WalletId!, token);

            // Exclude VTXOs locked by pending intents
            var lockedOutpoints = await intentStorage.GetLockedVtxoOutpoints(config.WalletId!, token);
            var lockedSet = new HashSet<NBitcoin.OutPoint>(lockedOutpoints);

            // Filter out excluded outpoints and locked VTXOs
            var excludeSet = request.ExcludeOutpoints?
                .Select(o => o.Trim())
                .ToHashSet() ?? new HashSet<string>();

            var availableCoins = allCoins
                .Where(c => !lockedSet.Contains(c.Outpoint) && !excludeSet.Contains($"{c.Outpoint.Hash}:{c.Outpoint.N}"))
                .ToList();

            if (!availableCoins.Any())
            {
                return Json(new SuggestCoinsResponse { Error = "No spendable coins available" });
            }

            // Separate by recoverability
            var nonRecoverable = availableCoins.Where(c => !c.Swept).ToList();
            var recoverable = availableCoins.Where(c => c.Swept).ToList();

            var response = new SuggestCoinsResponse();

            // Lightning requires non-recoverable coins only
            if (request.DestinationType == DestinationType.LightningInvoice)
            {
                if (!nonRecoverable.Any())
                {
                    return Json(new SuggestCoinsResponse
                    {
                        Error = "Lightning requires non-recoverable coins. No non-recoverable coins available."
                    });
                }

                response = SelectCoins(nonRecoverable, request.AmountSats, SpendType.Swap);
            }
            // Ark address: prefer offchain (non-recoverable), fallback to batch (recoverable)
            else if (request.DestinationType == DestinationType.ArkAddress)
            {
                // Try offchain first with non-recoverable
                if (nonRecoverable.Any())
                {
                    var offchainAttempt = SelectCoins(nonRecoverable, request.AmountSats, SpendType.Offchain);
                    if (offchainAttempt.Error == null)
                    {
                        response = offchainAttempt;
                    }
                    else if (recoverable.Any())
                    {
                        // Fallback to batch with all coins
                        response = SelectCoins(availableCoins, request.AmountSats, SpendType.Batch);
                        response.Warning = "Using batch mode (recoverable coins included)";
                    }
                    else
                    {
                        response = offchainAttempt; // Return the error
                    }
                }
                else if (recoverable.Any())
                {
                    // Only recoverable available - must use batch
                    response = SelectCoins(recoverable, request.AmountSats, SpendType.Batch);
                    response.Warning = "Offchain not available - only recoverable coins";
                }
                else
                {
                    response.Error = "No spendable coins available";
                }
            }
            // Bitcoin address: always batch
            else
            {
                response = SelectCoins(availableCoins, request.AmountSats, SpendType.Batch);
            }

            return Json(response);
        }
        catch (Exception ex)
        {
            return Json(new SuggestCoinsResponse { Error = ex.Message });
        }
    }

    /// <summary>
    /// Pre-flight validation before executing spend.
    /// </summary>
    [HttpPost("stores/{storeId}/validate-spend")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ValidateSpend(
        string storeId,
        [FromBody] ValidateSpendRequest request,
        CancellationToken token)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig(requireOwnedByStore: false);
        if (errorResult != null)
            return Json(new ValidateSpendResponse { Errors = { "Store not configured" } });

        var response = new ValidateSpendResponse();
        var hasLightning = false;
        var hasRecoverableCoins = false;

        // Validate coins exist and are spendable
        if (request.VtxoOutpoints.Any())
        {
            var outpoints = ParseOutpoints(request.VtxoOutpoints.ToArray());

            var vtxos = await vtxoStorage.GetVtxos(
                walletIds: [config!.WalletId!],
                outpoints: outpoints.ToList(),
                includeSpent: false,
                cancellationToken: token);

            if (vtxos.Count != request.VtxoOutpoints.Count)
            {
                response.Errors.Add("Some selected coins are no longer available");
            }

            hasRecoverableCoins = vtxos.Any(v => v.Swept);
        }
        else
        {
            response.Errors.Add("No coins selected");
        }

        // Get network for address parsing
        var serverInfo = await clientTransport.GetServerInfoAsync(token);
        var network = serverInfo.Network;

        // Validate each output
        for (int i = 0; i < request.Outputs.Count; i++)
        {
            var output = request.Outputs[i];
            var result = new OutputValidationResult { Index = i };

            if (string.IsNullOrWhiteSpace(output.Destination))
            {
                result.Error = "Destination required";
            }
            else
            {
                var destination = output.Destination.Trim();

                // Check for Lightning first (BOLT11, LNURL, Lightning Address)
                if (IsLightningDestination(destination))
                {
                    result.DetectedType = destination.IsValidEmail() ||
                        destination.StartsWith("lnurl", StringComparison.OrdinalIgnoreCase)
                        ? DestinationType.LnurlPay
                        : DestinationType.LightningInvoice;
                    hasLightning = true;
                }
                else
                {
                    // Use existing ParseOutputDestination helper
                    var spendOutput = new SpendOutputViewModel { Destination = destination };
                    var (dest, amount, outputType) = ParseOutputDestination(spendOutput, network);

                    if (dest == null)
                    {
                        result.Error = "Invalid address format";
                    }
                    else if (outputType == ArkTxOutType.Vtxo)
                    {
                        result.DetectedType = DestinationType.ArkAddress;
                    }
                    else
                    {
                        result.DetectedType = DestinationType.BitcoinAddress;
                    }
                }
            }

            response.OutputResults.Add(result);
        }

        // Cross-validation rules
        if (hasLightning)
        {
            if (request.Outputs.Count > 1)
            {
                response.Errors.Add("Lightning supports single output only");
            }
            if (hasRecoverableCoins)
            {
                response.Errors.Add("Lightning requires non-recoverable coins");
            }
            response.SpendType = SpendType.Swap;
        }
        else if (response.OutputResults.Any(r => r.DetectedType == DestinationType.BitcoinAddress))
        {
            response.SpendType = SpendType.Batch;
        }
        else if (hasRecoverableCoins)
        {
            response.SpendType = SpendType.Batch;
        }
        else
        {
            response.SpendType = SpendType.Offchain;
        }

        response.IsValid = !response.Errors.Any() && !response.OutputResults.Any(r => r.Error != null);
        return Json(response);
    }

    /// <summary>
    /// Unified Send Wizard - main entry point.
    /// </summary>
    [HttpGet("stores/{storeId}/send")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Send(
        string storeId,
        string? vtxos,
        string? destinations,
        string? destination,
        CancellationToken token)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig(requireOwnedByStore: false);
        if (errorResult != null)
            return errorResult;

        var model = new SendWizardViewModel
        {
            StoreId = storeId,
            VtxoOutpoints = vtxos,
            Destinations = destinations,
            Destination = destination
        };

        // Load balances
        model.Balances = await GetArkBalances(config!.WalletId!, token);

        // Load available (spendable) coins - get outpoints from ArkCoin, then fetch ArkVtxo details
        var allCoins = await arkadeSpender.GetAvailableCoins(config.WalletId!, token);

        // Exclude VTXOs locked by pending intents
        var lockedOutpoints = await intentStorage.GetLockedVtxoOutpoints(config.WalletId!, token);
        var lockedSet = new HashSet<NBitcoin.OutPoint>(lockedOutpoints);
        var spendableOutpoints = allCoins
            .Where(c => !lockedSet.Contains(c.Outpoint))
            .Select(c => c.Outpoint).ToList();

        if (!spendableOutpoints.Any())
            return View("Send", model);

        // Fetch full ArkVtxo details for the spendable coins
        var availableVtxos = await vtxoStorage.GetVtxos(
            outpoints: spendableOutpoints,
            walletIds: [config.WalletId!],
            includeSpent: false,
            cancellationToken: token);
        model.AvailableVtxos = availableVtxos.ToList();

        if (!model.AvailableVtxos.Any())
            return View("Send", model);

        // Handle pre-selected VTXOs from query param
        if (!string.IsNullOrEmpty(vtxos))
        {
            if (vtxos.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                // Special case: select all available VTXOs
                model.SelectedVtxos = model.AvailableVtxos.ToList();
                model.CoinSelectionMode = "manual";
            }
            else
            {
                var requestedOutpoints = vtxos.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .ToHashSet();

                model.SelectedVtxos = model.AvailableVtxos
                    .Where(v => requestedOutpoints.Contains($"{v.TransactionId}:{v.TransactionOutputIndex}"))
                    .ToList();

                model.CoinSelectionMode = "manual";

                // Warn if some requested coins unavailable
                if (model.SelectedVtxos.Count < requestedOutpoints.Count)
                {
                    var found = model.SelectedVtxos
                        .Select(v => $"{v.TransactionId}:{v.TransactionOutputIndex}")
                        .ToHashSet();
                    var missing = requestedOutpoints.Except(found).Count();
                    model.Errors.Add($"{missing} selected coin(s) no longer available");
                }
            }
        }

        // Handle pre-filled destinations (BIP21-aware parsing)
        if (!string.IsNullOrEmpty(destinations))
        {
            var serverInfo = await clientTransport.GetServerInfoAsync(token);
            var parsedDestinations = ParseDestinationsParam(destinations, serverInfo.Network);

            foreach (var parsed in parsedDestinations)
            {
                var isBip21 = parsed.Type is Send2DestinationType.Bip21Ark or Send2DestinationType.Bip21Lightning
                              || parsed.RawDestination.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase);
                var output = new SendOutputViewModel
                {
                    Destination = parsed.ResolvedAddress ?? parsed.RawDestination,
                    RawBip21 = isBip21 ? parsed.RawDestination : null,
                    ResolvedAddress = parsed.ResolvedAddress,
                    AmountBtc = parsed.AmountSats > 0 ? parsed.AmountBtc : null,
                    PayoutId = parsed.PayoutId,
                    IsBip21Parsed = isBip21,
                    IsReadonly = isBip21,
                    DetectedType = MapSend2TypeToDestinationType(parsed.Type),
                    IsLightning = parsed.Type is Send2DestinationType.LightningInvoice or Send2DestinationType.Bip21Lightning,
                    Error = parsed.Error
                };
                model.Outputs.Add(output);
            }
        }
        else if (!string.IsNullOrEmpty(destination))
        {
            var serverInfo = await clientTransport.GetServerInfoAsync(token);
            var parsed = ParseSend2Destination(destination, null, serverInfo.Network);
            var isBip21 = parsed.Type is Send2DestinationType.Bip21Ark or Send2DestinationType.Bip21Lightning
                          || destination.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase);
            var isLightning = parsed.Type is Send2DestinationType.LightningInvoice or Send2DestinationType.Bip21Lightning;

            model.Outputs.Add(new SendOutputViewModel
            {
                Destination = parsed.ResolvedAddress ?? parsed.RawDestination,
                RawBip21 = isBip21 ? destination : null,
                ResolvedAddress = parsed.ResolvedAddress,
                AmountBtc = parsed.AmountSats > 0 ? parsed.AmountBtc : null,
                PayoutId = parsed.PayoutId,
                IsBip21Parsed = isBip21,
                IsReadonly = isBip21 || isLightning,
                DetectedType = MapSend2TypeToDestinationType(parsed.Type),
                IsLightning = isLightning,
                Error = parsed.Error
            });
        }
        else
        {
            // Default: one empty output row
            model.Outputs.Add(new SendOutputViewModel());
        }

        return View("Send", model);
    }

    /// <summary>
    /// Execute the send transaction.
    /// </summary>
    [HttpPost("stores/{storeId}/send")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Send(
        string storeId,
        [FromForm] SendWizardViewModel model,
        [FromForm] string[] selectedVtxoOutpoints,
        [FromForm] string? SpendType,
        [FromForm] string? CoinSelectionMode,
        CancellationToken token)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig(requireOwnedByStore: false);
        if (errorResult != null)
            return errorResult;

        model.StoreId = storeId;
        model.Balances = await GetArkBalances(config!.WalletId!, token);

        // User's spend type preference (Arkade = offchain, Batch = onchain intent)
        var preferBatch = string.Equals(SpendType, "Batch", StringComparison.OrdinalIgnoreCase);

        // Re-load available coins for validation (excluding locked VTXOs)
        var allCoins = await arkadeSpender.GetAvailableCoins(config.WalletId!, token);
        var lockedOutpoints = await intentStorage.GetLockedVtxoOutpoints(config.WalletId!, token);
        var lockedSet = new HashSet<NBitcoin.OutPoint>(lockedOutpoints);
        var unlocked = allCoins.Where(c => !lockedSet.Contains(c.Outpoint)).ToList();
        var spendableOutpoints = unlocked.Select(c => c.Outpoint).ToList();
        var availableVtxos = await vtxoStorage.GetVtxos(
            outpoints: spendableOutpoints,
            walletIds: [config.WalletId!],
            includeSpent: false,
            cancellationToken: token);
        model.AvailableVtxos = availableVtxos.ToList();

        // Validate selected coins
        var isAutoMode = string.Equals(CoinSelectionMode, "auto", StringComparison.OrdinalIgnoreCase);

        if (!selectedVtxoOutpoints.Any() && !isAutoMode)
        {
            model.Errors.Add("No coins selected");
            return View("Send", model);
        }

        var selectedSet = selectedVtxoOutpoints.ToHashSet();
        var selectedCoins = unlocked
            .Where(c => selectedSet.Contains($"{c.Outpoint.Hash}:{c.Outpoint.N}"))
            .ToList();

        if (selectedCoins.Count != selectedVtxoOutpoints.Length && isAutoMode)
        {
            // Auto mode: re-select coins from available unlocked set
            selectedCoins = unlocked.ToList();
            selectedSet = selectedCoins
                .Select(c => $"{c.Outpoint.Hash}:{c.Outpoint.N}")
                .ToHashSet();
        }
        else if (selectedCoins.Count != selectedVtxoOutpoints.Length)
        {
            var missing = selectedVtxoOutpoints.Length - selectedCoins.Count;
            model.Errors.Add($"{missing} selected coin(s) are no longer available (spent or locked). Please re-select your coins and try again.");
            return View("Send", model);
        }

        if (!selectedCoins.Any())
        {
            model.Errors.Add("No coins available to spend");
            return View("Send", model);
        }

        model.SelectedVtxos = model.AvailableVtxos
            .Where(v => selectedSet.Contains($"{v.TransactionId}:{v.TransactionOutputIndex}"))
            .ToList();

        // Validate outputs - allow empty for consolidation
        var validOutputs = model.Outputs.Where(o => !string.IsNullOrWhiteSpace(o.Destination)).ToList();
        var isConsolidation = !validOutputs.Any();

        // Handle consolidation (no destination = send to self)
        if (isConsolidation)
        {
            try
            {
                var consolidationServerInfo = await clientTransport.GetServerInfoAsync(token);
                var consolidationTotalInput = selectedCoins.Sum(c => c.TxOut.Value.Satoshi);
                var hasRecoverableCoins = selectedCoins.Any(c => c.Swept);

                // Prevent pointless 1-in-1-out Arkade consolidation
                // With Arkade (not Batch) and only 1 non-recoverable coin, consolidation does nothing useful
                if (!preferBatch && !hasRecoverableCoins && selectedCoins.Count == 1)
                {
                    model.Errors.Add("Arkade consolidation with a single coin is not useful. Either select multiple coins to consolidate, use Batch mode to renew expiry, or enter a destination to send funds.");
                    return View("Send", model);
                }

                // Get the wallet's own Ark address for consolidation
                var contractOutput = await contractService.DeriveContract(config.WalletId!, NextContractPurpose.SendToSelf, ContractActivityState.Inactive, cancellationToken: token);
                var selfDest = contractOutput.GetArkAddress();

                // For recoverable coins OR user chose Batch, create an intent (batch transaction)
                if (hasRecoverableCoins || preferBatch)
                {
                    // Estimate fee for batch transaction
                    var consolidationOutputForFee = new ArkTxOut(
                        ArkTxOutType.Vtxo,
                        Money.Satoshis(consolidationTotalInput),
                        selfDest);
                    var feeEstimation = await feeEstimator.EstimateFeeAsync(
                        selectedCoins.ToArray(),
                        new[] { consolidationOutputForFee },
                        token);

                    var outputAmount = consolidationTotalInput - feeEstimation;
                    if (outputAmount <= 0)
                    {
                        model.Errors.Add("Insufficient funds after fees");
                        return View("Send", model);
                    }

                    var consolidationOutput = new ArkTxOut(
                        ArkTxOutType.Vtxo,
                        Money.Satoshis(outputAmount),
                        selfDest);

                    // Create intent for batch (automatically cancels any overlapping intents)
                    var intentTxId = await intentGenerationService.GenerateManualIntent(
                        config.WalletId!,
                        new ArkIntentSpec(
                            selectedCoins.ToArray(),
                            new [] { consolidationOutput },
                            null,
                            null
                        ),
                        cancellationToken: token);

                    var message = hasRecoverableCoins
                        ? $"Recovery intent created! Intent ID: {intentTxId}. Coins will be consolidated in the next batch round."
                        : $"Batch intent created! Intent ID: {intentTxId}. Coins will be consolidated in the next batch round.";

                    return RedirectWithSuccess(nameof(Intents), message, new { storeId });
                }

                // For non-recoverable coins with Arkade preference, use direct Arkade spend
                var arkadeOutput = new ArkTxOut(
                    ArkTxOutType.Vtxo,
                    Money.Satoshis(consolidationTotalInput),
                    selfDest);

                var txId = await arkadeSpender.Spend(
                    config.WalletId!,
                    selectedCoins.ToArray(),
                    new[] { arkadeOutput },
                    token);

                // Poll for VTXO updates
                var activeContracts = await contractStorage.GetContracts(walletIds: [config.WalletId!], isActive: true, cancellationToken: token);
                await vtxoSyncService.PollScriptsForVtxos(activeContracts.Select(c => c.Script).ToHashSet(), PostOpVtxoPollSince(), token);

                return RedirectWithSuccess(nameof(StoreOverview), $"Coins consolidated successfully! TxId: {txId}", new { storeId });
            }
            catch (Exception ex)
            {
                model.Errors.Add($"Consolidation failed: {ex.Message}");
                return View("Send", model);
            }
        }

        // Get server info for network (needed for Lightning and destination parsing)
        var serverInfo = await clientTransport.GetServerInfoAsync(token);

        // Check for Lightning (BOLT11, LNURL, or Lightning Address)
        var isLightning = validOutputs.Any(o => IsLightningDestination(o.Destination));

        if (isLightning)
        {
            if (validOutputs.Count > 1)
            {
                model.Errors.Add("Lightning supports single output only");
                return View("Send", model);
            }

            if (selectedCoins.Any(c => c.Swept))
            {
                model.Errors.Add("Lightning requires non-recoverable coins");
                return View("Send", model);
            }

            // Execute Lightning payment
            try
            {
                var lnOutput = validOutputs[0];
                var lnDestination = lnOutput.Destination;

                // Resolve LNURL/Lightning Address to BOLT11 at submit time
                if (lnDestination.IsValidEmail() ||
                    lnDestination.StartsWith("lnurl", StringComparison.OrdinalIgnoreCase))
                {
                    var amount = lnOutput.AmountSats ?? model.TotalSelectedSats;
                    var (bolt11, lnurlError) = await ResolveLnurlToInvoiceAsync(
                        lnDestination, amount, serverInfo.Network, token);
                    if (lnurlError != null)
                    {
                        model.Errors.Add($"LNURL resolution failed: {lnurlError}");
                        return View("Send", model);
                    }
                    lnDestination = bolt11!;
                }
                else
                {
                    lnDestination = lnDestination
                        .Replace("lightning:", "", StringComparison.OrdinalIgnoreCase);
                }

                await arkadeSpendingService.Spend(store!, lnDestination, token);

                // Mark payout as paid if this fulfills a payout
                if (!string.IsNullOrEmpty(lnOutput.PayoutId))
                    await MarkPayoutPaid(lnOutput.PayoutId, null, token);

                return RedirectWithSuccess(nameof(StoreOverview), "Lightning payment sent!", new { storeId });
            }
            catch (Exception ex)
            {
                model.Errors.Add($"Lightning payment failed: {ex.Message}");
                return View("Send", model);
            }
        }

        // Parse all destinations and build ArkTxOut array
        var totalInputAmount = selectedCoins.Sum(c => c.TxOut.Value.Satoshi);
        var arkOutputs = new List<ArkTxOut>();

        for (int i = 0; i < validOutputs.Count; i++)
        {
            var output = validOutputs[i];
            var spendOutput = new SpendOutputViewModel { Destination = output.Destination };
            var (dest, parsedAmount, outputType) = ParseOutputDestination(spendOutput, serverInfo.Network);

            if (dest == null)
            {
                output.Error = "Invalid address format";
                model.Errors.Add($"Output {i + 1}: Invalid address format");
                continue;
            }

            // Amount priority: user-specified > destination-specified > (single output: send all)
            var outputAmount = output.AmountSats.HasValue
                ? Money.Satoshis(output.AmountSats.Value)
                : parsedAmount;

            if (outputAmount == null || outputAmount == Money.Zero)
            {
                if (validOutputs.Count == 1)
                {
                    // Single output with no amount - send all
                    outputAmount = Money.Satoshis(totalInputAmount);
                }
                else
                {
                    output.Error = "Amount is required";
                    model.Errors.Add($"Output {i + 1}: Amount is required");
                    continue;
                }
            }

            arkOutputs.Add(new ArkTxOut(outputType, outputAmount, dest));
        }

        if (model.Errors.Any())
        {
            return View("Send", model);
        }

        // Determine if batch is required (on-chain outputs or user preference)
        var hasOnchainOutput = arkOutputs.Any(o => o.Type == ArkTxOutType.Onchain);
        var useBatch = preferBatch || hasOnchainOutput;

        // Execute the spend
        try
        {
            if (useBatch)
            {
                // Batch path: create an intent for the next batch round
                // Need to add a change output back to self for the remainder after fees
                var totalOutput = arkOutputs.Sum(o => o.Value.Satoshi);

                // Build preliminary outputs to estimate fees (include a placeholder change output)
                var contractOutput = await contractService.DeriveContract(config.WalletId!, NextContractPurpose.SendToSelf, ContractActivityState.AwaitingFundsBeforeDeactivate, cancellationToken: token);
                var selfDest = contractOutput.GetArkAddress();

                // Estimate fees with all outputs including change
                var preliminaryOutputs = arkOutputs.ToList();
                var preliminaryChange = totalInputAmount - totalOutput;
                if (preliminaryChange > 0)
                {
                    preliminaryOutputs.Add(new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(preliminaryChange), selfDest));
                }

                var feeEstimation = await feeEstimator.EstimateFeeAsync(
                    selectedCoins.ToArray(),
                    preliminaryOutputs.ToArray(),
                    token);

                var changeAmount = totalInputAmount - totalOutput - feeEstimation;
                if (changeAmount < 0)
                {
                    model.Errors.Add($"Insufficient funds. Need {totalOutput + feeEstimation} sats but only have {totalInputAmount} sats.");
                    return View("Send", model);
                }

                // Build final outputs: destination(s) + change (if any)
                var finalOutputs = arkOutputs.ToList();
                if (changeAmount > 0)
                {
                    finalOutputs.Add(new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(changeAmount), selfDest));
                }

                var intentTxId = await intentGenerationService.GenerateManualIntent(
                    config.WalletId!,
                    new ArkIntentSpec(
                        selectedCoins.ToArray(),
                        finalOutputs.ToArray(),
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow.AddHours(1)
                    ),
                    cancellationToken: token);

                // Mark payouts as paid if any outputs fulfill payouts (no txId yet — assigned at batch time)
                foreach (var output in validOutputs.Where(o => !string.IsNullOrEmpty(o.PayoutId)))
                {
                    await MarkPayoutPaid(output.PayoutId!, null, token);
                }

                return RedirectWithSuccess(nameof(Intents),
                    $"Batch intent created! Intent ID: {intentTxId}. Transaction will be included in the next batch round.",
                    new { storeId });
            }
            else
            {
                // Arkade path: instant offchain spend
                var txId = await arkadeSpender.Spend(
                    config.WalletId!,
                    selectedCoins.ToArray(),
                    arkOutputs.ToArray(),
                    token);

                // Poll for VTXO updates
                var activeContracts = await contractStorage.GetContracts(walletIds: [config.WalletId!], isActive: true, cancellationToken: token);
                await vtxoSyncService.PollScriptsForVtxos(activeContracts.Select(c => c.Script).ToHashSet(), PostOpVtxoPollSince(), token);

                // Mark payouts as paid if any outputs fulfill payouts
                foreach (var output in validOutputs.Where(o => !string.IsNullOrEmpty(o.PayoutId)))
                {
                    await MarkPayoutPaid(output.PayoutId!, txId, token);
                }

                return RedirectWithSuccess(nameof(StoreOverview), $"Transaction sent successfully! TxId: {txId}", new { storeId });
            }
        }
        catch (Exception ex)
        {
            model.Errors.Add($"Transaction failed: {ex.Message}");
            return View("Send", model);
        }
    }

    private static SuggestCoinsResponse SelectCoins(
        List<ArkCoin> coins,
        long? targetSats,
        SpendType spendType)
        => ArkSpendHelpers.SelectCoins(coins, targetSats, spendType);

    private async Task<List<ArkCoin>> GetCoinsForOutpoints(string walletId, List<string> outpoints, CancellationToken token)
    {
        var coins = new List<ArkCoin>();
        var availableCoins = await arkadeSpender.GetAvailableCoins(walletId, token);

        foreach (var outpointStr in outpoints)
        {
            var parts = outpointStr.Split(':');
            if (parts.Length != 2) continue;

            var txId = parts[0];
            if (!uint.TryParse(parts[1], out var vout)) continue;

            var coin = availableCoins.FirstOrDefault(c =>
                c.Outpoint.Hash.ToString() == txId && c.Outpoint.N == vout);

            if (coin != null)
            {
                coins.Add(coin);
            }
        }

        return coins;
    }
}
