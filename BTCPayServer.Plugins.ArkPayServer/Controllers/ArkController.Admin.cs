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
    [HttpGet("~/ark-admin/wallet/{walletId}")]
    [Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> AdminWalletOverview(string walletId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(walletId))
            return NotFound();

        // Check if wallet exists
        var adminWallet = await walletStorage.GetWalletById(walletId, cancellationToken);
        if (adminWallet == null)
            return RedirectWithError(nameof(ListWallets), "Wallet not found.");

        var destination = adminWallet.Destination;
        var balances = await GetArkBalances(walletId, cancellationToken);
        var signerAvailable = await walletProvider.GetAddressProviderAsync(walletId, cancellationToken) != null;

        // Get the default/active contract address
        string? defaultAddress = null;
        var adminActiveContracts = await contractStorage.GetContracts(walletIds: [walletId], isActive: true, take: 1, cancellationToken: cancellationToken);
        var adminActiveContract = adminActiveContracts.FirstOrDefault();
        if (adminActiveContract != null)
        {
            var terms = await clientTransport.GetServerInfoAsync(cancellationToken);
            var script = Script.FromHex(adminActiveContract.Script);
            var serverKey = OutputDescriptorHelpers.Extract(terms.SignerKey).XOnlyPubKey;
            var address = ArkAddress.FromScriptPubKey(script, serverKey);
            defaultAddress = address.ToString(terms.Network.ChainName == ChainName.Mainnet);
        }

        // Check Ark Operator connection using helper
        var (arkOperatorConnected, arkOperatorError) = await CheckServiceConnectionAsync(
            ct => clientTransport.GetServerInfoAsync(ct), cancellationToken);

        // Check Boltz connection using helper
        var (boltzConnected, boltzError) = boltzClient != null
            ? await CheckServiceConnectionAsync(ct => boltzClient.GetVersionAsync(), cancellationToken)
            : (false, null);

        ViewData["IsAdminView"] = true;
        ViewData["WalletId"] = walletId;

        return View("StoreOverview", new StoreOverviewViewModel
        {
            IsDestinationSweepEnabled = destination is not null,
            IsLightningEnabled = false, // Admin view doesn't check Lightning
            Balances = balances,
            WalletId = walletId,
            Destination = destination,
            SignerAvailable = signerAvailable,
            Wallet = adminWallet.Secret,
            DefaultAddress = defaultAddress,
            ArkOperatorUrl = arkNetworkConfig.ArkUri,
            ArkOperatorConnected = arkOperatorConnected,
            ArkOperatorError = ArkOperatorAvailability.DescribeMessage(arkOperatorError),
            BoltzUrl = arkNetworkConfig.BoltzUri,
            BoltzConnected = boltzConnected,
            BoltzError = boltzError
        });
    }

    [HttpGet("~/ark-admin/wallets")]
    [Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> ListWallets(CancellationToken cancellationToken)
    {
        var wallets = await GetWalletsWithDetailsAsync(cancellationToken);
        return View(wallets);
    }

    [HttpPost("~/ark-admin/wallet/{walletId}/delete")]
    [Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> AdminDeleteWallet(string walletId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(walletId))
            return NotFound();

        try
        {
            // Check if wallet exists
            var wallet = await GetWalletWithDetailsAsync(walletId, cancellationToken);
            if (wallet == null)
                return RedirectWithError(nameof(ListWallets), "Wallet not found.");

            // Check if wallet has any pending swaps
            var hasPendingSwaps = await HasPendingSwapsAsync(walletId, cancellationToken);
            if (hasPendingSwaps)
                return RedirectWithError(nameof(AdminWalletOverview), "Cannot delete wallet: It has pending swaps.", new { walletId });

            // Check if wallet has any pending intents
            var hasPendingIntents = await HasPendingIntentsAsync(walletId, cancellationToken);
            if (hasPendingIntents)
                return RedirectWithError(nameof(AdminWalletOverview), "Cannot delete wallet: It has pending intents.", new { walletId });

            // Delete the wallet and all associated data
            await walletStorage.DeleteWallet(walletId, cancellationToken);
            return RedirectWithSuccess(nameof(ListWallets), $"Wallet {walletId} and all associated data deleted successfully.");
        }
        catch (Exception ex)
        {
            return RedirectWithError(nameof(AdminWalletOverview), $"Failed to delete wallet: {ex.Message}", new { walletId });
        }
    }
}
