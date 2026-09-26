using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

public partial class ArkGreenfieldController
{
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
}
