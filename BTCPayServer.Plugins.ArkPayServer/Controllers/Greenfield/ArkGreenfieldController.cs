using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Core.Wallet;
using NArk.Hosting;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

/// <summary>
/// Greenfield REST API for Arkade wallet operations.
/// All endpoints require API key authentication and store-scoped permissions.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[EnableCors(CorsPolicies.All)]
public partial class ArkGreenfieldController(
    ArkNetworkConfig arkNetworkConfig,
    StoreRepository storeRepository,
    PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
    IClientTransport clientTransport,
    ArkadeSpendingService arkadeSpendingService,
    ISpendingService arkadeSpender,
    IFeeEstimator feeEstimator,
    IContractService contractService,
    NArk.Core.Recovery.ISingleKeyDefaultEnsurer singleKeyDefaultEnsurer,
    IBitcoinBlockchain bitcoinTimeChainProvider,
    VtxoSynchronizationService vtxoSyncService,
    IContractStorage contractStorage,
    IVtxoStorage vtxoStorage,
    IWalletStorage walletStorage,
    ArkLightningSpendKeyService spendKeyService,
    IWalletProvider walletProvider,
    IIntentStorage intentStorage,
    BoardingUtxoSyncService boardingUtxoSyncService,
    IHttpClientFactory httpClientFactory,
    ArkadeSolverService arkadeSolver,
    ArkadeSwapRefresher swapRefresher) : ControllerBase
{
    private string? CurrentStoreId => HttpContext.GetStoreDataOrNull()?.Id;

    #region Helpers

    private (ArkadePaymentMethodConfig? config, IActionResult? error) GetStoreConfig()
    {
        var store = HttpContext.GetStoreDataOrNull();
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
        var store = HttpContext.GetStoreDataOrNull();
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

    /// <summary>
    /// Resolves wallet input into wallet info, following the same logic as ArkController.GetFromInputWallet.
    /// Returns: (walletInfo if new wallet needs creating, walletId, isNewlyGenerated, mnemonic if generated).
    /// </summary>
    private async Task<(ArkWalletInfo? WalletInfo, string? WalletId, bool IsNew, string? Mnemonic)> ResolveWalletInput(
        string? wallet, string? destination, CancellationToken cancellationToken)
    {
        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);

        // Empty input → generate a new wallet
        if (string.IsNullOrWhiteSpace(wallet))
        {
            var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
            var mnemonicStr = mnemonic.ToString();
            var walletInfo = await WalletFactory.CreateWallet(mnemonicStr, destination, serverInfo, cancellationToken);
            return (walletInfo, walletInfo.Id, true, mnemonicStr);
        }

        // nsec import
        if (wallet.StartsWith("nsec", StringComparison.OrdinalIgnoreCase))
        {
            // Check if wallet already exists
            var candidateIds = new[] { WalletFactory.GetOutputDescriptorFromNsec(wallet) }
                .Concat(WalletFactory.GetAlternateWalletIdsFromNsec(wallet));
            foreach (var candidateId in candidateIds)
            {
                var existing = await walletStorage.GetWalletById(candidateId, cancellationToken);
                if (existing != null)
                    return (null, candidateId, false, null);
            }

            var walletInfo = await WalletFactory.CreateWallet(wallet, destination, serverInfo, cancellationToken);
            return (walletInfo, walletInfo.Id, true, null);
        }

        // BIP-39 mnemonic (12 or 24 words)
        var words = wallet.Trim().Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is 12 or 24)
        {
            try
            {
                var mnemonic = new Mnemonic(wallet.Trim(), Wordlist.English);
                var walletInfo = await WalletFactory.CreateWallet(
                    mnemonic.ToString(), destination, serverInfo, cancellationToken);
                return (walletInfo, walletInfo.Id, true, null);
            }
            catch
            {
                // Not a valid mnemonic, fall through
            }
        }

        // Ark address → generate wallet with destination
        if (ArkAddress.TryParse(wallet, out var addr))
        {
            var serverKey = serverInfo.SignerKey.Extract().XOnlyPubKey;
            if (!serverKey.ToBytes().SequenceEqual(addr!.ServerKey.ToBytes()))
                throw new InvalidOperationException("Ark address server key does not match the connected operator.");

            var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
            var mnemonicStr = mnemonic.ToString();
            var walletInfo = await WalletFactory.CreateWallet(mnemonicStr, wallet, serverInfo, cancellationToken);
            return (walletInfo, walletInfo.Id, true, mnemonicStr);
        }

        // Existing wallet ID
        var existingWallet = await walletStorage.GetWalletById(wallet, cancellationToken);
        if (existingWallet != null)
            return (null, wallet, false, null);

        throw new InvalidOperationException(
            "Unsupported wallet input. Provide a BIP-39 mnemonic (12/24 words), nsec key, Ark address, or existing wallet ID.");
    }

    private async Task<bool> ConfigureLightning(StoreData store, string walletId, bool generatedByStore,
        CancellationToken cancellationToken)
    {
        var lightningPaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var existingLnConfig = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(
            lightningPaymentMethodId, paymentMethodHandlerDictionary);
        if (existingLnConfig != null) return false;

        var lnurlPaymentMethodId = PaymentTypes.LNURL.GetPaymentMethodId("BTC");

        var lnConfig = new LightningPaymentMethodConfig
        {
            ConnectionString = generatedByStore
                ? await spendKeyService.BuildConnectionStringAsync(walletId, cancellationToken)
                : ArkLightningSpendKeyService.BuildReceiveOnlyConnectionString(walletId),
        };

        store.SetPaymentMethodConfig(paymentMethodHandlerDictionary[lightningPaymentMethodId], lnConfig);
        store.SetPaymentMethodConfig(paymentMethodHandlerDictionary[lnurlPaymentMethodId], new LNURLPaymentMethodConfig
        {
            UseBech32Scheme = true,
            LUD12Enabled = true
        });

        return true;
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


