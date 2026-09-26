using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.ArkPayServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NArk.Abstractions;
using NArk.Core.Contracts;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

public partial class ArkController
{
    /// <summary>
    /// Receive page: shows existing manual receive address or prompts to generate one.
    /// </summary>
    [HttpGet("stores/{storeId}/receive")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Receive(string storeId, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        var model = new ArkReceiveViewModel();

        try
        {
            var existingAddress = await FindManualReceiveAddress(config!.WalletId!, cancellationToken);
            if (existingAddress != null)
                model.Address = existingAddress;

            var existingBoarding = await FindManualBoardingAddress(config.WalletId!, cancellationToken);
            if (existingBoarding != null)
                model.BoardingAddress = existingBoarding;
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = DescribeArkError(ex, "Failed to check receive address");
        }

        return View(model);
    }

    [HttpPost("stores/{storeId}/receive")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Receive(string storeId, string command, CancellationToken cancellationToken)
    {
        var (store, config, errorResult) = await ValidateStoreAndConfig();
        if (errorResult != null) return errorResult;

        try
        {
            var model = new ArkReceiveViewModel();
            var terms = await clientTransport.GetServerInfoAsync(cancellationToken);

            if (command == "generate-boarding-address")
            {
                var boardingContract = (ArkBoardingContract)await contractService.DeriveContract(
                    config!.WalletId!,
                    NextContractPurpose.Boarding,
                    ContractActivityState.AwaitingFundsBeforeDeactivate,
                    metadata: new Dictionary<string, string> { ["Source"] = "manual" },
                    cancellationToken: cancellationToken);
                model.BoardingAddress = boardingContract.GetOnchainAddress(terms.Network).ToString();

                // Preserve existing ark address if any
                var existingAddress = await FindManualReceiveAddress(config.WalletId!, cancellationToken);
                if (existingAddress != null) model.Address = existingAddress;
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

                // Preserve existing boarding address if any
                var existingBoarding = await FindManualBoardingAddress(config.WalletId!, cancellationToken);
                if (existingBoarding != null) model.BoardingAddress = existingBoarding;
            }

            return View(model);
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = DescribeArkError(ex, "Failed to generate address");
        }

        return RedirectToAction(nameof(Receive), new { storeId });
    }

    private async Task<string?> FindManualReceiveAddress(string walletId, CancellationToken cancellationToken)
    {
        var existingContracts = await contractStorage.GetContracts(
            walletIds: [walletId],
            isActive: true,
            contractTypes: [ArkPaymentContract.ContractType],
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
                c.Metadata?.GetValueOrDefault("Source") is "manual" or "manual-boarding");

        if (boardingEntity == null) return null;

        var terms = await clientTransport.GetServerInfoAsync(cancellationToken);
        var boardingContract = (ArkBoardingContract)ArkContractParser.Parse(boardingEntity.Type, boardingEntity.AdditionalData, terms.Network)!;
        return boardingContract.GetOnchainAddress(terms.Network).ToString();
    }
}
