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
