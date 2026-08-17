using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NBitcoin;

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
            SolverRelayUrl = arkadeSolver.RelayUri,
            SolverPubkey = arkadeSolver.SolverPubkey,
            SolverConfigured = arkadeSolver.IsConfigured,
            SolverCanReceive = arkadeSolver.CanReceive
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
