using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Security;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Hosting;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Models;
using NBitcoin;
using NBitcoin.Scripting;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

/// <summary>
/// Greenfield REST API for Arkade wallet operations.
/// All endpoints require API key authentication and store-scoped permissions.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[EnableCors(CorsPolicies.All)]
public class ArkGreenfieldController(
    ArkNetworkConfig arkNetworkConfig,
    PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
    IClientTransport clientTransport,
    ArkadeSpendingService arkadeSpendingService,
    ISpendingService arkadeSpender,
    IContractService contractService,
    IChainTimeProvider bitcoinTimeChainProvider,
    VtxoSynchronizationService vtxoSyncService,
    IContractStorage contractStorage,
    ISwapStorage swapStorage,
    IVtxoStorage vtxoStorage,
    IWalletStorage walletStorage,
    IWalletProvider walletProvider,
    IIntentStorage intentStorage,
    BoardingUtxoSyncService boardingUtxoSyncService,
    BoltzLimitsValidator? boltzLimitsValidator) : ControllerBase
{
    private string? CurrentStoreId => HttpContext.GetStoreData()?.Id;

    #region Wallet

    /// <summary>
    /// Get Arkade wallet information for a store.
    /// </summary>
    [HttpGet("~/api/v1/stores/{storeId}/arkade/wallet")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> GetWallet(string storeId, CancellationToken cancellationToken)
    {
        var (config, error) = GetStoreConfig();
        if (error != null) return error;

        var wallet = await walletStorage.GetWalletById(config!.WalletId!, cancellationToken);
        var signerAvailable = await walletProvider.GetAddressProviderAsync(config.WalletId!, cancellationToken) != null;

        string? defaultAddress = null;
        if (wallet?.WalletType == WalletType.SingleKey)
        {
            try
            {
                var terms = await clientTransport.GetServerInfoAsync(cancellationToken);
                var descriptor = OutputDescriptor.Parse(wallet.AccountDescriptor, terms.Network);
                var defaultContract = new ArkPaymentContract(terms.SignerKey, terms.UnilateralExit, descriptor);
                defaultAddress = defaultContract.GetArkAddress().ToString(terms.Network.ChainName == ChainName.Mainnet);
            }
            catch
            {
                // Operator unavailable — skip default address
            }
        }

        return Ok(new ArkWalletData
        {
            WalletId = config.WalletId!,
            WalletType = (wallet?.WalletType ?? WalletType.SingleKey).ToString(),
            SignerAvailable = signerAvailable,
            IsOwnedByStore = config.GeneratedByStore,
            DefaultAddress = defaultAddress,
            Destination = wallet?.Destination,
            AllowSubDustAmounts = config.AllowSubDustAmounts,
            BoardingEnabled = config.BoardingEnabled,
            MinBoardingAmountSats = config.MinBoardingAmountSats,
            LightningEnabled = IsArkadeLightningEnabled()
        });
    }

    #endregion

    #region Balance

    /// <summary>
    /// Get Arkade wallet balance breakdown.
    /// </summary>
    [HttpGet("~/api/v1/stores/{storeId}/arkade/balance")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> GetBalance(string storeId, CancellationToken cancellationToken)
    {
        var (config, error) = GetStoreConfig();
        if (error != null) return error;

        try
        {
            var balance = await ComputeBalances(config!.WalletId!, cancellationToken);
            return Ok(balance);
        }
        catch (Exception ex)
        {
            return this.CreateAPIError(503, "balance-unavailable",
                $"Unable to compute balance: {ex.Message}");
        }
    }

    #endregion

    #region Receive Address

    /// <summary>
    /// Get or generate an Ark receive address.
    /// </summary>
    [HttpGet("~/api/v1/stores/{storeId}/arkade/address")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> GetAddress(string storeId, CancellationToken cancellationToken)
    {
        var (config, error) = GetStoreConfig();
        if (error != null) return error;

        var model = new ArkAddressData();

        var existingAddress = await FindManualReceiveAddress(config!.WalletId!, cancellationToken);
        if (existingAddress != null)
            model.Address = existingAddress;

        var existingBoarding = await FindManualBoardingAddress(config.WalletId!, cancellationToken);
        if (existingBoarding != null)
            model.BoardingAddress = existingBoarding;

        return Ok(model);
    }

    /// <summary>
    /// Generate a new Ark receive address (off-chain or boarding).
    /// </summary>
    [HttpPost("~/api/v1/stores/{storeId}/arkade/address")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> CreateAddress(string storeId,
        [FromQuery] string type = "offchain", CancellationToken cancellationToken = default)
    {
        var (config, error) = GetStoreConfig();
        if (error != null) return error;

        var terms = await clientTransport.GetServerInfoAsync(cancellationToken);
        var model = new ArkAddressData();

        if (type == "boarding")
        {
            var boardingContract = (ArkBoardingContract)await contractService.DeriveContract(
                config!.WalletId!,
                NextContractPurpose.Boarding,
                ContractActivityState.AwaitingFundsBeforeDeactivate,
                metadata: new Dictionary<string, string> { ["Source"] = "manual-boarding" },
                cancellationToken: cancellationToken);
            model.BoardingAddress = boardingContract.GetOnchainAddress(terms.Network).ToString();

            // Include existing ark address if any
            var existing = await FindManualReceiveAddress(config.WalletId!, cancellationToken);
            if (existing != null) model.Address = existing;
        }
        else
        {
            var contract = await contractService.DeriveContract(
                config!.WalletId!,
                NextContractPurpose.Receive,
                ContractActivityState.AwaitingFundsBeforeDeactivate,
                metadata: new Dictionary<string, string> { ["Source"] = "manual" },
                cancellationToken: cancellationToken);
            model.Address = contract.GetArkAddress().ToString(terms.Network.ChainName == ChainName.Mainnet);

            // Include existing boarding address if any
            var existing = await FindManualBoardingAddress(config.WalletId!, cancellationToken);
            if (existing != null) model.BoardingAddress = existing;
        }

        return Ok(model);
    }

    #endregion

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
            var store = HttpContext.GetStoreData();
            var txId = await arkadeSpendingService.Spend(store!, request.Destination, cancellationToken);

            return Ok(new ArkSendResponse { TxId = txId });
        }
        catch (Exception ex)
        {
            return this.CreateAPIError("send-failed", ex.Message);
        }
    }

    #endregion

    #region VTXOs

    /// <summary>
    /// List VTXOs for the store's Arkade wallet.
    /// </summary>
    [HttpGet("~/api/v1/stores/{storeId}/arkade/vtxos")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> ListVtxos(string storeId,
        [FromQuery] bool includeSpent = false,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        var (config, error) = GetStoreConfig();
        if (error != null) return error;

        take = Math.Min(take, 500);

        var vtxos = await vtxoStorage.GetVtxos(
            walletIds: [config!.WalletId!],
            includeSpent: includeSpent,
            skip: skip,
            take: take,
            cancellationToken: cancellationToken);

        var currentTime = await bitcoinTimeChainProvider.GetChainTime(cancellationToken);
        var allCoins = await arkadeSpender.GetAvailableCoins(config.WalletId!, cancellationToken);
        var spendableOutpoints = allCoins.Select(c => c.Outpoint).ToHashSet();
        var coinByOutpoint = allCoins.ToDictionary(c => c.Outpoint);

        var result = vtxos.Select(v =>
        {
            var outpoint = v.OutPoint;
            var isSpendable = spendableOutpoints.Contains(outpoint);
            var coin = coinByOutpoint.GetValueOrDefault(outpoint);

            return new ArkVtxoData
            {
                Outpoint = $"{v.TransactionId}:{v.TransactionOutputIndex}",
                AmountSats = (long)v.Amount,
                Script = v.Script,
                IsSpent = v.IsSpent(),
                IsSpendable = isSpendable,
                IsRecoverable = coin?.IsRecoverable(currentTime) ?? false,
                IsBoarding = coin?.Unrolled ?? false,
                CommitmentTxId = v.CommitmentTxids?.FirstOrDefault(),
                ExpiresAt = v.ExpiresAt,
                Assets = v.Assets?.Select(a => new ArkVtxoAssetData
                {
                    AssetId = a.AssetId,
                    Amount = a.Amount
                }).ToList()
            };
        }).ToList();

        return Ok(result);
    }

    #endregion

    #region Intents

    /// <summary>
    /// List intents (pending transactions) for the store's wallet.
    /// </summary>
    [HttpGet("~/api/v1/stores/{storeId}/arkade/intents")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> ListIntents(string storeId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        [FromQuery] string? state = null,
        CancellationToken cancellationToken = default)
    {
        var (config, error) = GetStoreConfig();
        if (error != null) return error;

        take = Math.Min(take, 500);

        ArkIntentState[]? stateFilter = null;
        if (!string.IsNullOrEmpty(state) && Enum.TryParse<ArkIntentState>(state, true, out var parsedState))
            stateFilter = [parsedState];

        var intents = await intentStorage.GetIntents(
            walletIds: [config!.WalletId!],
            skip: skip,
            take: take,
            states: stateFilter,
            cancellationToken: cancellationToken);

        var result = intents.Select(i => new ArkIntentData
        {
            IntentId = i.IntentId,
            IntentTxId = i.IntentTxId,
            WalletId = i.WalletId,
            State = i.State.ToString(),
            CreatedAt = i.CreatedAt,
            ValidFrom = i.ValidFrom,
            ValidUntil = i.ValidUntil,
            BatchId = i.BatchId,
            CommitmentTransactionId = i.CommitmentTransactionId,
            CancellationReason = i.CancellationReason,
            Vtxos = i.IntentVtxos?.Select(v => new ArkIntentVtxoData
            {
                Outpoint = v.ToString()
            }).ToList() ?? new()
        }).ToList();

        return Ok(result);
    }

    /// <summary>
    /// Cancel a pending intent.
    /// </summary>
    [HttpDelete("~/api/v1/stores/{storeId}/arkade/intents/{intentTxId}")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> CancelIntent(string storeId, string intentTxId,
        CancellationToken cancellationToken)
    {
        var (config, error) = GetStoreConfig();
        if (error != null) return error;

        try
        {
            var intents = await intentStorage.GetIntents(
                walletIds: [config!.WalletId!],
                intentTxIds: [intentTxId],
                cancellationToken: cancellationToken);

            var intent = intents.FirstOrDefault();
            if (intent == null)
                return this.CreateAPIError(404, "intent-not-found", "Intent not found.");

            // If intent was submitted to server, delete from server
            if (intent.State == ArkIntentState.WaitingForBatch)
            {
                try { await clientTransport.DeleteIntent(intent, cancellationToken); }
                catch { /* Continue — we still mark cancelled in storage */ }
            }

            // Update storage to mark as cancelled
            await intentStorage.SaveIntent(intent.WalletId, intent with
            {
                State = ArkIntentState.Cancelled,
                CancellationReason = "Cancelled via API",
                UpdatedAt = DateTimeOffset.UtcNow
            }, cancellationToken);

            return Ok();
        }
        catch (Exception ex)
        {
            return this.CreateAPIError("cancel-failed", $"Failed to cancel intent: {ex.Message}");
        }
    }

    #endregion

    #region Contracts

    /// <summary>
    /// List contracts (address derivations) for the store's wallet.
    /// </summary>
    [HttpGet("~/api/v1/stores/{storeId}/arkade/contracts")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> ListContracts(string storeId,
        [FromQuery] bool activeOnly = false,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        var (config, error) = GetStoreConfig();
        if (error != null) return error;

        take = Math.Min(take, 500);

        var contracts = await contractStorage.GetContracts(
            walletIds: [config!.WalletId!],
            isActive: activeOnly ? true : null,
            cancellationToken: cancellationToken);

        // Get VTXO counts per contract script
        var unspentVtxos = await vtxoStorage.GetVtxos(
            walletIds: [config.WalletId!],
            includeSpent: false,
            cancellationToken: cancellationToken);
        var vtxoCountByScript = unspentVtxos
            .GroupBy(v => v.Script)
            .ToDictionary(g => g.Key, g => g.Count());

        var result = contracts
            .Skip(skip)
            .Take(take)
            .Select(c => new ArkContractData
            {
                Script = c.Script,
                WalletId = c.WalletIdentifier,
                ContractType = c.Type,
                ActivityState = c.ActivityState.ToString(),
                CreatedAt = c.CreatedAt,
                Metadata = c.Metadata,
                VtxoCount = vtxoCountByScript.GetValueOrDefault(c.Script)
            }).ToList();

        return Ok(result);
    }

    #endregion

    #region Swaps

    /// <summary>
    /// List swaps (Lightning/chain) for the store's wallet.
    /// </summary>
    [HttpGet("~/api/v1/stores/{storeId}/arkade/swaps")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> ListSwaps(string storeId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        var (config, error) = GetStoreConfig();
        if (error != null) return error;

        take = Math.Min(take, 500);

        ArkSwapStatus[]? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<ArkSwapStatus>(status, true, out var parsedStatus))
            statusFilter = [parsedStatus];

        var swaps = await swapStorage.GetSwaps(
            walletIds: [config!.WalletId!],
            skip: skip,
            take: take,
            status: statusFilter,
            cancellationToken: cancellationToken);

        var result = swaps.Select(s => new ArkSwapData
        {
            SwapId = s.SwapId,
            WalletId = s.WalletId,
            Type = s.SwapType.ToString(),
            Status = s.Status.ToString(),
            AmountSats = s.ExpectedAmount,
            CreatedAt = s.CreatedAt,
            Metadata = s.Metadata
        }).ToList();

        return Ok(result);
    }

    #endregion

    #region Server Info

    /// <summary>
    /// Get Ark operator server information.
    /// </summary>
    [HttpGet("~/api/v1/stores/{storeId}/arkade/server-info")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> GetServerInfo(string storeId, CancellationToken cancellationToken)
    {
        var (_, error) = GetStoreConfig();
        if (error != null) return error;

        try
        {
            var info = await clientTransport.GetServerInfoAsync(cancellationToken);
            return Ok(new ArkServerInfoData
            {
                Network = info.Network.Name,
                DustSats = info.Dust.Satoshi,
                SignerPubKey = Convert.ToHexString(info.SignerKey.Extract().XOnlyPubKey.ToBytes()).ToLowerInvariant(),
                UnilateralExitBlocks = (int)info.UnilateralExit.Value,
                BoardingExitBlocks = (int)info.BoardingExit.Value,
                ForfeitAddress = info.ForfeitAddress?.ToString(),
                MaxOpReturnOutputs = info.MaxOpReturnOutputs,
                MaxTxWeight = info.MaxTxWeight
            });
        }
        catch (Exception ex)
        {
            return this.CreateAPIError(503, "operator-unavailable",
                $"Cannot reach Ark operator: {ex.Message}");
        }
    }

    #endregion

    #region Status

    /// <summary>
    /// Get overall Arkade service status for a store.
    /// </summary>
    [HttpGet("~/api/v1/stores/{storeId}/arkade/status")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> GetStatus(string storeId, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreData();
        if (store == null) return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);
        var status = new ArkStatusData
        {
            IsConfigured = config?.WalletId != null
        };

        // Check Ark operator
        try
        {
            await clientTransport.GetServerInfoAsync(cancellationToken);
            status.ArkOperator = new ArkServiceConnectionData
            {
                Url = arkNetworkConfig.ArkUri,
                IsConnected = true
            };
        }
        catch (Exception ex)
        {
            status.ArkOperator = new ArkServiceConnectionData
            {
                Url = arkNetworkConfig.ArkUri,
                IsConnected = false,
                Error = ex.Message
            };
        }

        // Check Boltz
        if (boltzLimitsValidator != null)
        {
            try
            {
                var limits = await boltzLimitsValidator.GetAllLimitsAsync(cancellationToken);
                status.Boltz = new ArkServiceConnectionData
                {
                    Url = arkNetworkConfig.BoltzUri,
                    IsConnected = limits != null,
                    Error = limits == null ? "Boltz instance does not support Ark" : null
                };
            }
            catch (Exception ex)
            {
                status.Boltz = new ArkServiceConnectionData
                {
                    Url = arkNetworkConfig.BoltzUri,
                    IsConnected = false,
                    Error = ex.Message
                };
            }
        }

        // Blockchain info
        try
        {
            var (timestamp, height) = await bitcoinTimeChainProvider.GetChainTime(cancellationToken);
            status.Blockchain = new ArkBlockchainData
            {
                Height = height,
                Timestamp = timestamp
            };
        }
        catch
        {
            // Skip blockchain info if unavailable
        }

        return Ok(status);
    }

    #endregion

    #region Boltz Limits

    /// <summary>
    /// Get Boltz swap limits and fees.
    /// </summary>
    [HttpGet("~/api/v1/stores/{storeId}/arkade/boltz-limits")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> GetBoltzLimits(string storeId, CancellationToken cancellationToken)
    {
        var (_, error) = GetStoreConfig();
        if (error != null) return error;

        if (boltzLimitsValidator == null)
            return this.CreateAPIError(404, "boltz-not-configured", "Boltz integration is not configured.");

        try
        {
            var limits = await boltzLimitsValidator.GetAllLimitsAsync(cancellationToken);
            if (limits == null)
                return this.CreateAPIError(503, "boltz-unavailable", "Boltz instance does not support Ark.");

            return Ok(new ArkBoltzLimitsData
            {
                Submarine = new ArkSwapLimitData
                {
                    MinAmountSats = limits.SubmarineMinAmount,
                    MaxAmountSats = limits.SubmarineMaxAmount,
                    FeePercentage = limits.SubmarineFeePercentage,
                    MinerFeeSats = limits.SubmarineMinerFee
                },
                Reverse = new ArkSwapLimitData
                {
                    MinAmountSats = limits.ReverseMinAmount,
                    MaxAmountSats = limits.ReverseMaxAmount,
                    FeePercentage = limits.ReverseFeePercentage,
                    MinerFeeSats = limits.ReverseMinerFee
                }
            });
        }
        catch (Exception ex)
        {
            return this.CreateAPIError(503, "boltz-unavailable", $"Cannot reach Boltz: {ex.Message}");
        }
    }

    #endregion

    #region Sync

    /// <summary>
    /// Trigger a wallet sync (VTXOs + boarding UTXOs).
    /// </summary>
    [HttpPost("~/api/v1/stores/{storeId}/arkade/sync")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> SyncWallet(string storeId, CancellationToken cancellationToken)
    {
        var (config, error) = GetStoreConfig();
        if (error != null) return error;

        try
        {
            var contracts = await contractStorage.GetContracts(
                walletIds: [config!.WalletId!], cancellationToken: cancellationToken);
            await vtxoSyncService.PollScriptsForVtxos(
                contracts.Select(c => c.Script).ToHashSet(), cancellationToken);
            await boardingUtxoSyncService.SyncAsync(cancellationToken);
            return Ok(new { synced = true });
        }
        catch (Exception ex)
        {
            return this.CreateAPIError("sync-failed", $"Sync failed: {ex.Message}");
        }
    }

    #endregion

    #region Helpers

    private (ArkadePaymentMethodConfig? config, IActionResult? error) GetStoreConfig()
    {
        var store = HttpContext.GetStoreData();
        if (store == null)
            return (null, this.CreateAPIError(404, "store-not-found", "Store not found."));

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);
        if (config?.WalletId is null)
            return (null, this.CreateAPIError(404, "arkade-not-configured",
                "Arkade wallet is not configured for this store."));

        return (config, null);
    }

    private T? GetConfig<T>(PaymentMethodId paymentMethodId, StoreData store) where T : class
    {
        return store.GetPaymentMethodConfig<T>(paymentMethodId, paymentMethodHandlerDictionary);
    }

    private bool IsArkadeLightningEnabled()
    {
        var store = HttpContext.GetStoreData();
        if (store == null) return false;
        var lnConfig = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(
            PaymentTypes.LN.GetPaymentMethodId("BTC"), paymentMethodHandlerDictionary);
        return lnConfig?.ConnectionString?.StartsWith("type=arkade", StringComparison.InvariantCultureIgnoreCase) is true;
    }

    private async Task<ArkBalanceData> ComputeBalances(string walletId, CancellationToken cancellationToken)
    {
        var currentTime = await bitcoinTimeChainProvider.GetChainTime(cancellationToken);
        var allCoins = await arkadeSpender.GetAvailableCoins(walletId, cancellationToken);

        var coinsByRecoverableStatus = allCoins.ToLookup(coin => coin.IsRecoverable(currentTime));

        var allSpendableOutpoints = allCoins.Select(coin => coin.Outpoint).ToHashSet();

        var all = await vtxoStorage.GetVtxos(
            walletIds: [walletId],
            includeSpent: false,
            cancellationToken: cancellationToken);

        var unspendableBalance = all
            .Where(vtxo => !allSpendableOutpoints.Contains(vtxo.OutPoint))
            .Sum(vtxo => (long)vtxo.Amount);

        var availableBalance = coinsByRecoverableStatus[false]
            .Where(coin => !coin.Unrolled)
            .Sum(coin => coin.Amount.Satoshi);
        var recoverableBalance = coinsByRecoverableStatus[true].Sum(coin => coin.Amount.Satoshi);
        var boardingBalance = allCoins.Where(coin => coin.Unrolled).Sum(coin => coin.Amount.Satoshi);

        // Locked: VTXOs committed to active intents
        var lockedOutpoints = await intentStorage.GetLockedVtxoOutpoints(walletId, cancellationToken);
        var lockedSet = new HashSet<OutPoint>(lockedOutpoints);
        var lockedBalance = coinsByRecoverableStatus[false]
            .Where(coin => !coin.Unrolled && lockedSet.Contains(coin.Outpoint))
            .Sum(coin => coin.Amount.Satoshi);

        return new ArkBalanceData
        {
            AvailableSats = availableBalance - lockedBalance,
            LockedSats = lockedBalance,
            RecoverableSats = recoverableBalance,
            UnspendableSats = unspendableBalance,
            BoardingSats = boardingBalance
        };
    }

    private async Task<string?> FindManualReceiveAddress(string walletId, CancellationToken cancellationToken)
    {
        var existingContracts = await contractStorage.GetContracts(
            walletIds: [walletId],
            isActive: true,
            cancellationToken: cancellationToken);

        var manualContract = existingContracts
            .FirstOrDefault(c =>
                c.ActivityState == ContractActivityState.AwaitingFundsBeforeDeactivate &&
                c.Metadata?.GetValueOrDefault("Source") == "manual");

        if (manualContract == null) return null;

        var terms = await clientTransport.GetServerInfoAsync(cancellationToken);
        var script = Script.FromHex(manualContract.Script);
        var serverKey = terms.SignerKey.Extract().XOnlyPubKey;
        var arkAddr = ArkAddress.FromScriptPubKey(script, serverKey);
        return arkAddr.ToString(terms.Network.ChainName == ChainName.Mainnet);
    }

    private async Task<string?> FindManualBoardingAddress(string walletId, CancellationToken cancellationToken)
    {
        var existingContracts = await contractStorage.GetContracts(
            walletIds: [walletId],
            isActive: true,
            contractTypes: [ArkBoardingContract.ContractType],
            cancellationToken: cancellationToken);

        var boardingEntity = existingContracts
            .FirstOrDefault(c =>
                c.ActivityState == ContractActivityState.AwaitingFundsBeforeDeactivate &&
                c.Metadata?.GetValueOrDefault("Source") == "manual-boarding");

        if (boardingEntity == null) return null;

        var terms = await clientTransport.GetServerInfoAsync(cancellationToken);
        var boardingContract = (ArkBoardingContract)ArkContractParser.Parse(
            boardingEntity.Type, boardingEntity.AdditionalData, terms.Network)!;
        return boardingContract.GetOnchainAddress(terms.Network).ToString();
    }

    #endregion
}
