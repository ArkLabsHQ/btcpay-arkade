using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.ArkPayServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NArk.Abstractions.Intents;
using NArk.Core.Contracts;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using NArk.Swaps.Models;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

public partial class ArkController
{
    [HttpGet("stores/{storeId}/contracts")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Contracts(
        string storeId,
        string? searchTerm = null,
        string? searchText = null,
        int skip = 0,
        int count = 50,
        bool debug = false)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (!config!.GeneratedByStore)
            return View(new StoreContractsViewModel { StoreId = storeId });

        // Get status filter using helper
        var activeFilter = ParseBooleanFilter(searchTerm, "status", "active");

        // Get contracts with pagination
        var contracts = await contractStorage.GetContracts(
            walletIds: [config.WalletId],
            isActive: activeFilter,
            searchText: searchText,
            skip: skip,
            take: count,
            cancellationToken: HttpContext.RequestAborted);

        // Get VTXOs for the contracts (include spent and recoverable for full history)
        var contractVtxos = new Dictionary<string, ArkVtxo[]>();
        if (contracts.Any())
        {
            var contractScripts = contracts.Select(c => c.Script).ToList();
            var vtxos = await vtxoStorage.GetVtxos(
                scripts: contractScripts,
                walletIds: [config.WalletId],
                includeSpent: true,
                cancellationToken: HttpContext.RequestAborted);

            contractVtxos = vtxos
                .GroupBy(v => v.Script)
                .ToDictionary(g => g.Key, g => g.ToArray());
        }

        // Always load swaps
        var contractSwaps = new Dictionary<string, NArk.Swaps.Models.ArkSwap[]>();
        if (contracts.Any())
        {
            var contractScripts = contracts.Select(c => c.Script).ToArray();
            var swaps = await swapStorage.GetSwaps(
                walletIds: [config.WalletId!],
                contractScripts: contractScripts,
                cancellationToken: HttpContext.RequestAborted);
            contractSwaps = swaps
                .GroupBy(s => s.ContractScript)
                .ToDictionary(g => g.Key, g => g.ToArray());
        }

        var model = new StoreContractsViewModel
        {
            StoreId = storeId,
            Contracts = contracts,
            Skip = skip,
            Count = count,
            SearchText = searchText,
            Search = new SearchString(searchTerm),
            ContractVtxos = contractVtxos,
            ContractSwaps = contractSwaps,
            CanManageContracts = config.GeneratedByStore,
            Debug = debug,
            CachedContractScripts = (await contractStorage.GetContracts(walletIds: [config.WalletId], isActive: true, cancellationToken: HttpContext.RequestAborted))
                .Select(c => c.Script).ToHashSet(),
            ListenedScripts = debug ? vtxoSyncService.ListenedScripts.ToHashSet() : []
        };

        return View(model);
    }

    [HttpGet("stores/{storeId}/swaps")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    /// <summary>
    /// Lists the store's Arkade Lightning swaps.
    /// </summary>
    /// <remarks>
    /// Read straight from intent storage rather than through the Lightning client. The client is
    /// scoped to one wallet's connection string and refuses without a spend capability; this page is
    /// a merchant looking at their own store, which the controller has already authorised.
    /// </remarks>
    [HttpGet("stores/{storeId}/lightning-swaps")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> LightningSwaps(
        string storeId,
        string? searchTerm = null,
        int skip = 0,
        int count = 50)
    {
        var (_, config, errorResult) = await ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (!config!.GeneratedByStore || arkadeIntentStorage is null)
        {
            return View(new StoreLightningSwapsViewModel
            {
                StoreId = storeId,
                SolverConfigured = arkadeSolver.IsConfigured
            });
        }

        var statusFilter = ParseEnumFilter<ArkadeSwapIntentStatus>(searchTerm, "status", s => s switch
        {
            "pending" => ArkadeSwapIntentStatus.Pending,
            "claimable" => ArkadeSwapIntentStatus.Claimable,
            "fulfilled" => ArkadeSwapIntentStatus.Fulfilled,
            "refundable" => ArkadeSwapIntentStatus.Refundable,
            "cancelled" => ArkadeSwapIntentStatus.Cancelled,
            _ => null
        });

        var swaps = await arkadeIntentStorage.GetArkadeSwapIntents(
            status: statusFilter,
            walletIds: [config.WalletId!],
            cancellationToken: HttpContext.RequestAborted);

        return View(new StoreLightningSwapsViewModel
        {
            StoreId = storeId,
            SolverConfigured = arkadeSolver.IsConfigured,
            Search = new SearchString(searchTerm ?? string.Empty),
            SearchText = searchTerm,
            Skip = skip,
            Count = count,
            Swaps = swaps
                .OrderByDescending(i => i.CreatedAt)
                .Skip(skip)
                .Take(count)
                .ToList()
        });
    }

    public async Task<IActionResult> Swaps(
        string storeId,
        string? searchTerm = null,
        string? searchText = null,
        int skip = 0,
        int count = 50,
        bool debug = false)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (!config!.GeneratedByStore)
            return View(new StoreSwapsViewModel { StoreId = storeId });

        // Hidden from the navigation once a store has no pre-migration swaps, so a request that
        // still arrives is a stale bookmark rather than a choice. Send it to the page that replaced
        // this one instead of rendering a table that can never fill.
        if (!await legacySwaps.HasLegacySwapsAsync(config.WalletId, HttpContext.RequestAborted))
            return RedirectToAction(nameof(LightningSwaps), new { storeId });

        // Get status filter using helper
        var statusFilter = ParseEnumFilter<ArkSwapStatus>(searchTerm, "status", s => s switch
        {
            "pending" => ArkSwapStatus.Pending,
            "settled" => ArkSwapStatus.Settled,
            "failed" => ArkSwapStatus.Failed,
            _ => null
        });

        // Get type filter using helper
        var typeFilter = ParseEnumFilter<ArkSwapType>(searchTerm, "type", t => t switch
        {
            "reverse" => ArkSwapType.ReverseSubmarine,
            "submarine" => ArkSwapType.Submarine,
            _ => null
        });

        var swaps = await swapStorage.GetSwaps(
            walletIds: [config.WalletId!],
            status: statusFilter != null ? [statusFilter.Value] : null,
            swapTypes: typeFilter != null ? [typeFilter.Value] : null,
            searchText: searchText,
            skip: skip,
            take: count,
            cancellationToken: HttpContext.RequestAborted);

        // Get contracts for the swaps to display contract details
        var swapContractScripts = swaps.Select(s => s.ContractScript).Distinct().ToArray();
        var swapContracts = await contractStorage.GetContracts(
            walletIds: [config.WalletId!],
            scripts: swapContractScripts,
            cancellationToken: HttpContext.RequestAborted);

        var model = new StoreSwapsViewModel
        {
            StoreId = storeId,
            Swaps = swaps,
            SwapContracts = swapContracts.ToDictionary(c => c.Script),
            Skip = skip,
            Count = count,
            SearchText = searchText,
            Search = new SearchString(searchTerm),
            Debug = debug
        };

        return View(model);
    }

    [HttpGet("stores/{storeId}/vtxos")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Vtxos(
        string storeId,
        string? searchTerm = null,
        string? searchText = null,
        int skip = 0,
        int count = 50)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (!config!.GeneratedByStore)
            return View(new StoreVtxosViewModel { StoreId = storeId });

        // Parse status filters - default to unspent and recoverable if no filter is set
        var search = new SearchString(searchTerm);
        bool includeSpent = false;
        bool filterRecoverableOnly = false;
        bool filterNonRecoverableOnly = false;
        bool? spendableFilter = null; // null = all, true = spendable only, false = non-spendable only

        if (search.ContainsFilter("status"))
        {
            var statusFilters = search.GetFilterArray("status");
            includeSpent = statusFilters.Contains("spent");
            var hasRecoverable = statusFilters.Contains("recoverable");
            var hasUnspent = statusFilters.Contains("unspent");

            // Determine recoverable filtering based on UI selection
            if (hasRecoverable && !hasUnspent)
            {
                filterRecoverableOnly = true;
            }
            else if (hasUnspent && !hasRecoverable)
            {
                filterNonRecoverableOnly = true;
            }
            // If both or neither, show all (no recoverable filter)

            // Check for spendable filter
            var hasSpendable = statusFilters.Contains("spendable");
            var hasNonSpendable = statusFilters.Contains("non-spendable");

            if (hasSpendable && hasNonSpendable)
            {
                // Both selected = show all (no filter)
                spendableFilter = null;
            }
            else if (hasSpendable)
            {
                spendableFilter = true;
            }
            else if (hasNonSpendable)
            {
                spendableFilter = false;
            }
        }
        else
        {
            // Default: show unspent and recoverable
            searchTerm = "status:unspent,status:recoverable";
            search = new SearchString(searchTerm);
        }

        // Get contract scripts for the wallet and fetch VTXOs
        var allContracts = await contractStorage.GetContracts(walletIds: [config.WalletId], cancellationToken: HttpContext.RequestAborted);
        var vtxoContractScripts = allContracts.Select(c => c.Script).ToList();
        var vtxos = await vtxoStorage.GetVtxos(
            scripts: vtxoContractScripts,
            walletIds: [config.WalletId],
            includeSpent: includeSpent,
            searchText: searchText,
            skip: skip,
            take: count,
            cancellationToken: HttpContext.RequestAborted);

        // Apply recoverable filter in-memory if needed
        if (filterRecoverableOnly)
        {
            vtxos = vtxos.Where(v => v.Swept).ToList();
        }
        else if (filterNonRecoverableOnly)
        {
            vtxos = vtxos.Where(v => !v.Swept).ToList();
        }

        // Get spendable coins to determine which VTXOs are actually spendable
        var spendableCoins = await arkadeSpender.GetAvailableCoins(config.WalletId, HttpContext.RequestAborted);
        var spendableOutpoints = spendableCoins
            .Select(coin => coin.Outpoint)
            .ToHashSet();

        // Apply spendable filter if specified
        if (spendableFilter.HasValue)
        {
            vtxos = vtxos
                .Where(vtxo =>
                {
                    var outpoint = new OutPoint(uint256.Parse(vtxo.TransactionId), (uint)vtxo.TransactionOutputIndex);
                    var isSpendable = spendableOutpoints.Contains(outpoint);
                    return spendableFilter.Value ? isSpendable : !isSpendable;
                })
                .ToList();
        }

        // Get contract info for all VTXO scripts
        var vtxoScripts = vtxos.Select(v => v.Script).Distinct().ToArray();
        var vtxoContractsQuery = await contractStorage.GetContracts(
            walletIds: [config.WalletId],
            scripts: vtxoScripts,
            cancellationToken: HttpContext.RequestAborted);
        var vtxoContracts = vtxoContractsQuery.ToDictionary(c => c.Script);

        var model = new StoreVtxosViewModel
        {
            StoreId = storeId,
            Vtxos = vtxos,
            SpendableOutpoints = spendableOutpoints,
            VtxoContracts = vtxoContracts,
            Skip = skip,
            Count = count,
            SearchText = searchText,
            SearchTerm = searchTerm,
            Search = search
        };

        return View(model);
    }

    [HttpGet("stores/{storeId}/intents")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Intents(
        string storeId,
        string? searchTerm = null,
        string? searchText = null,
        int skip = 0,
        int count = 50)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (!config!.GeneratedByStore)
            return View(new StoreIntentsViewModel { StoreId = storeId });

        // Get state filter using helper
        var stateFilter = ParseEnumFilter<ArkIntentState>(searchTerm, "state", s => s switch
        {
            "waiting-submit" => ArkIntentState.WaitingToSubmit,
            "waiting-batch" => ArkIntentState.WaitingForBatch,
            "batch-succeeded" => ArkIntentState.BatchSucceeded,
            "batch-failed" => ArkIntentState.BatchFailed,
            "cancelled" => ArkIntentState.Cancelled,
            _ => null
        });

        var intents = await intentStorage.GetIntents(
            walletIds: [config.WalletId!],
            states: stateFilter != null ? [stateFilter.Value] : null,
            searchText: searchText,
            skip: skip,
            take: count,
            cancellationToken: HttpContext.RequestAborted);

        // Get VTXOs referenced by intents so the view can show them
        var intentVtxoOutpoints = new Dictionary<string, OutPoint[]>();
        if (intents.Any())
        {
            foreach (var intent in intents)
            {
                if (intent.IntentVtxos.Length > 0)
                    intentVtxoOutpoints[intent.IntentTxId] = intent.IntentVtxos;
            }
        }

        // Fetch full VTXO data for all referenced outpoints
        var allOutpoints = intentVtxoOutpoints.Values.SelectMany(ops => ops).Distinct().ToArray();
        var vtxoLookup = new Dictionary<OutPoint, ArkVtxo>();
        if (allOutpoints.Length > 0)
        {
            var vtxos = await vtxoStorage.GetVtxos(outpoints: allOutpoints, includeSpent: true, cancellationToken: HttpContext.RequestAborted);
            vtxoLookup = vtxos.ToDictionary(v => v.OutPoint);
        }

        return View(new StoreIntentsViewModel
        {
            StoreId = storeId,
            Intents = intents,
            Skip = skip,
            Count = count,
            SearchText = searchText,
            Search = new SearchString(searchTerm),
            IntentVtxoOutpoints = intentVtxoOutpoints,
            VtxoLookup = vtxoLookup
        });
    }

    [HttpPost("stores/{storeId}/cancel-intent")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> CancelIntent(string storeId, string intentTxId, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        try
        {
            // Get the intent from storage - filter by wallet to prevent cross-wallet access
            var intents = await intentStorage.GetIntents(
                walletIds: [config!.WalletId],
                intentTxIds: [intentTxId],
                cancellationToken: cancellationToken);
            var intent = intents.FirstOrDefault();
            if (intent == null)
                return RedirectWithError(nameof(Intents), "Intent not found.", new { storeId });

            // If intent was submitted, delete from server
            if (intent.State == ArkIntentState.WaitingForBatch)
            {
                try
                {

                    await clientTransport.DeleteIntent(intent, cancellationToken);
                }
                catch (Exception e)
                {
                    // Log and continue - we will still mark as cancelled in storage even if server deletion fails
                    
                }
            }

            // Update storage to mark as cancelled
            await intentStorage.SaveIntent(intent.WalletId, intent with
            {
                State = NArk.Abstractions.Intents.ArkIntentState.Cancelled,
                CancellationReason = "User requested cancellation",
                UpdatedAt = DateTimeOffset.UtcNow
            }, cancellationToken);

            return RedirectWithSuccess(nameof(Intents), "Intent cancelled successfully.", new { storeId });
        }
        catch (InvalidOperationException ex)
        {
            return RedirectWithError(nameof(Intents), ex.Message, new { storeId });
        }
        catch (Exception ex)
        {
            return RedirectWithError(nameof(Intents), $"Failed to cancel intent: {ex.Message}", new { storeId });
        }
    }

    [HttpPost("stores/{storeId}/sync-contract")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> SyncContract(string storeId, string script, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        try
        {
            var contracts = await contractStorage.GetContracts(walletIds: [config!.WalletId], scripts: [script], cancellationToken: cancellationToken);
            if (!contracts.Any())
                return RedirectWithError(nameof(Contracts), "Contract not found.", new { storeId });

            await vtxoSyncService.PollScriptsForVtxos(contracts.Select(c => c.Script).ToHashSet(), cancellationToken);
            return RedirectWithSuccess(nameof(Contracts), "Contract VTXOs updated successfully.", new { storeId });
        }
        catch (Exception ex)
        {
            return RedirectWithError(nameof(Contracts), $"Failed to sync contract: {ex.Message}", new { storeId });
        }
    }

    [HttpPost("stores/{storeId}/delete-contract")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> DeleteContract(string storeId, string script, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        // Only allow deletion if wallet is generated by store
        if (!config!.GeneratedByStore)
            return RedirectWithError(nameof(Contracts), "Cannot delete contract: Wallet is not managed by this store.", new { storeId });

        try
        {
            var contracts = await contractStorage.GetContracts(walletIds: [config.WalletId], scripts: [script], cancellationToken: cancellationToken);
            if (!contracts.Any())
                return RedirectWithError(nameof(Contracts), "Contract not found.", new { storeId });

            // Check if contract has any pending swaps
            var swaps = await swapStorage.GetSwaps(walletIds: [config.WalletId!], contractScripts: [script], status: [ArkSwapStatus.Pending], cancellationToken: cancellationToken);
            if (swaps.Any())
                return RedirectWithError(nameof(Contracts), "Cannot delete contract: It has pending swaps.", new { storeId });

            // Delete the contract (cascade will delete related swaps)
            await contractStorage.DeleteContract(config.WalletId, script, cancellationToken);
            return RedirectWithSuccess(nameof(Contracts), "Contract deleted successfully.", new { storeId });
        }
        catch (Exception ex)
        {
            return RedirectWithError(nameof(Contracts), $"Failed to delete contract: {ex.Message}", new { storeId });
        }
    }

    [HttpPost("stores/{storeId}/import-contract")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ImportContract(string storeId, string contractString, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        // Only allow import if wallet is generated by store
        if (!config!.GeneratedByStore)
            return RedirectWithError(nameof(Contracts), "Cannot import contract: Wallet is not managed by this store.", new { storeId });

        if (string.IsNullOrWhiteSpace(contractString))
            return RedirectWithError(nameof(Contracts), "Contract string is required.", new { storeId });

        try
        {
            var terms = await clientTransport.GetServerInfoAsync(cancellationToken);

            // Parse the contract string to validate it
            var arkContract = ArkContractParser.Parse(contractString, terms.Network);
            if (arkContract == null)
                return RedirectWithError(nameof(Contracts), "Failed to parse contract. Invalid contract type or data.", new { storeId });

            var script = arkContract.GetArkAddress().ScriptPubKey;
            var scriptHex = script.ToHex();

            // Check if contract already exists
            var existingContracts = await contractStorage.GetContracts(walletIds: [config.WalletId], scripts: [scriptHex], cancellationToken: cancellationToken);
            if (existingContracts.Any())
                return RedirectWithError(nameof(Contracts), "Contract already exists in this wallet.", new { storeId });

            // Create the contract using ToEntity and save via storage
            var contractEntity = arkContract.ToEntity(config.WalletId);
            await contractStorage.SaveContract(contractEntity, cancellationToken);

            // Sync the wallet to detect any VTXOs for this contract
            var allContracts = await contractStorage.GetContracts(walletIds: [config.WalletId], cancellationToken: cancellationToken);
            await vtxoSyncService.PollScriptsForVtxos(allContracts.Select(c => c.Script).ToHashSet(), cancellationToken);

            return RedirectWithSuccess(nameof(Contracts), $"Contract imported successfully: {arkContract.GetArkAddress().ToString(terms.Network.ChainName == ChainName.Mainnet)}", new { storeId });
        }
        catch (Exception ex)
        {
            return RedirectWithError(nameof(Contracts), $"Failed to import contract: {ex.Message}", new { storeId });
        }
    }



    [HttpPost("stores/{storeId}/vtxos/mass-action")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> MassActionVtxos(string storeId, string command, string[] selectedItems, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (selectedItems.Length == 0)
            return RedirectWithError(nameof(Vtxos), "No items selected.", new { storeId });

        try
        {
            switch (command)
            {
                case "build-intent":
                case "build-transaction":
                    // Redirect to new unified Send wizard
                    return RedirectToAction(nameof(Send), new { storeId, vtxos = string.Join(",", selectedItems) });

                case "refresh-state":
                    // Look up selected VTXOs to get their scripts, then resolve contracts
                    var outpoints = selectedItems
                        .Select(s => NBitcoin.OutPoint.Parse(s.Replace('-', ':')))
                        .ToArray();
                    var selectedVtxos = await vtxoStorage.GetVtxos(
                        outpoints: outpoints, includeSpent: true, cancellationToken: cancellationToken);
                    var vtxoScripts = selectedVtxos.Select(v => v.Script).Distinct().ToArray();
                    var boardingContracts = await contractStorage.GetContracts(
                        scripts: vtxoScripts, scope: ContractScope.Onchain, cancellationToken: cancellationToken);
                    var nonBoardingScripts = (await contractStorage.GetContracts(
                            scripts: vtxoScripts, scope: ContractScope.Offchain, cancellationToken: cancellationToken))
                        .Select(c => c.Script).ToHashSet();
                    if (nonBoardingScripts.Count > 0)
                        await vtxoSyncService.PollScriptsForVtxos(nonBoardingScripts, cancellationToken);
                    if (boardingContracts.Count > 0)
                        await boardingUtxoSyncService.SyncAsync(boardingContracts, cancellationToken);
                    return RedirectWithSuccess(nameof(Vtxos), $"Refreshed state for {selectedItems.Length} VTXOs.", new { storeId });

                default:
                    return RedirectWithError(nameof(Vtxos), $"Unknown command: {command}", new { storeId });
            }
        }
        catch (Exception ex)
        {
            return RedirectWithError(nameof(Vtxos), $"Mass action failed: {ex.Message}", new { storeId });
        }
    }

    [HttpPost("stores/{storeId}/contracts/mass-action")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> MassActionContracts(string storeId, string command, string[] selectedItems, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (selectedItems.Length == 0)
            return RedirectWithError(nameof(Contracts), "No items selected.", new { storeId });

        try
        {
            switch (command)
            {
                case "sync-selected":
                    // Poll scripts for VTXO updates, routing boarding contracts to UTXO provider
                    var selectedBoarding = await contractStorage.GetContracts(
                        scripts: selectedItems, scope: ContractScope.Onchain, cancellationToken: cancellationToken);
                    var selectedNonBoardingScripts = (await contractStorage.GetContracts(
                            scripts: selectedItems, scope: ContractScope.Offchain, cancellationToken: cancellationToken))
                        .Select(c => c.Script).ToHashSet();
                    if (selectedNonBoardingScripts.Count > 0)
                        await vtxoSyncService.PollScriptsForVtxos(selectedNonBoardingScripts, cancellationToken);
                    if (selectedBoarding.Count > 0)
                        await boardingUtxoSyncService.SyncAsync(selectedBoarding, cancellationToken);
                    return RedirectWithSuccess(nameof(Contracts), $"Synced {selectedItems.Length} contracts.", new { storeId });

                case "set-active":
                    foreach (var script in selectedItems)
                    {
                        await contractStorage.UpdateContractActivityState(config!.WalletId, script, ContractActivityState.Active, cancellationToken);
                    }
                    return RedirectWithSuccess(nameof(Contracts), $"Set {selectedItems.Length} contracts to Active.", new { storeId });

                case "set-inactive":
                    foreach (var script in selectedItems)
                    {
                        await contractStorage.UpdateContractActivityState(config!.WalletId, script, ContractActivityState.Inactive, cancellationToken);
                    }
                    return RedirectWithSuccess(nameof(Contracts), $"Set {selectedItems.Length} contracts to Inactive.", new { storeId });

                case "set-awaiting":
                    foreach (var script in selectedItems)
                    {
                        await contractStorage.UpdateContractActivityState(config!.WalletId, script, ContractActivityState.AwaitingFundsBeforeDeactivate, cancellationToken);
                    }
                    return RedirectWithSuccess(nameof(Contracts), $"Set {selectedItems.Length} contracts to Awaiting Funds.", new { storeId });

                default:
                    return RedirectWithError(nameof(Contracts), $"Unknown command: {command}", new { storeId });
            }
        }
        catch (Exception ex)
        {
            return RedirectWithError(nameof(Contracts), $"Mass action failed: {ex.Message}", new { storeId });
        }
    }

    [HttpPost("stores/{storeId}/contracts/vtxos-sublist/mass-action")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> MassActionVtxosSublist(string storeId, string contractScript, string command, string[] selectedItems, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        if (selectedItems.Length == 0)
            return RedirectWithError(nameof(Contracts), "No items selected.", new { storeId });

        try
        {
            switch (command)
            {
                case "build-intent":
                    // Redirect to spend/intent builder with selected VTXOs
                    return RedirectToAction(nameof(SpendOverview), new { storeId, vtxoOutpoints = string.Join(",", selectedItems) });

                default:
                    return RedirectWithError(nameof(Contracts), $"Unknown command: {command}", new { storeId });
            }
        }
        catch (Exception ex)
        {
            return RedirectWithError(nameof(Contracts), $"Mass action failed: {ex.Message}", new { storeId });
        }
    }

    /// <summary>
    /// Parses outpoint strings (txid:index) into OutPoint objects.
    /// </summary>
    private static HashSet<OutPoint> ParseOutpoints(string[] outpointStrings)
    {
        var outpoints = new HashSet<OutPoint>();
        foreach (var str in outpointStrings)
        {
            var parts = str.Split(':');
            if (parts.Length == 2 && uint256.TryParse(parts[0], out var txid) && uint.TryParse(parts[1], out var index))
            {
                outpoints.Add(new OutPoint(txid, index));
            }
        }
        return outpoints;
    }
}
