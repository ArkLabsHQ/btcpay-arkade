using BTCPayServer.Data;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Plugins.ArkPayServer.Controllers;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Mvc;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Transport;
using NArk.Hosting;
using NBitcoin.Scripting;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;

namespace BTCPayServer.Plugins.ArkPayServer.ViewComponents;

public class ArkDashboardWidgetViewComponent(
    StoreRepository storeRepository,
    PaymentMethodHandlerDictionary handlerDictionary,
    IClientTransport clientTransport,
    ArkNetworkConfig arkNetworkConfig,
    ArkController arkController,
    IVtxoStorage vtxoStorage,
    IIntentStorage intentStorage,
    IWalletStorage walletStorage,
    BoltzClient? boltzClient = null,
    BoltzLimitsValidator? boltzLimitsValidator = null) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(StoreDashboardViewModel dashboardModel)
    {
        if (string.IsNullOrEmpty(dashboardModel?.StoreId))
            return Content(string.Empty);

        try
        {
            var store = await storeRepository.FindStore(dashboardModel.StoreId);
            if (store == null)
                return Content(string.Empty);

            var config = store.GetPaymentMethodConfig<ArkadePaymentMethodConfig>(
                ArkadePlugin.ArkadePaymentMethodId, handlerDictionary, true);

            if (config == null || string.IsNullOrEmpty(config.WalletId))
                return Content(string.Empty);

            var walletId = config.WalletId!;
            var ct = HttpContext.RequestAborted;

            var model = new StoreOverviewViewModel
            {
                StoreId = dashboardModel.StoreId,
                WalletId = walletId,
                Balances = await arkController.GetArkBalances(walletId, ct)
            };

            // Fetch stats in parallel: VTXO count, pending intent count, wallet info, service connections
            var vtxoTask = vtxoStorage.GetVtxos(walletIds: [walletId], includeSpent: false, cancellationToken: ct);
            var pendingIntentsTask = intentStorage.GetIntents(
                walletIds: [walletId], take: 100,
                states: [ArkIntentState.WaitingToSubmit, ArkIntentState.WaitingForBatch, ArkIntentState.BatchInProgress],
                cancellationToken: ct);
            var walletTask = walletStorage.GetWalletById(walletId, ct);

            await Task.WhenAll(vtxoTask, pendingIntentsTask, walletTask);

            var unspentVtxos = await vtxoTask;
            model.TotalVtxoCount = unspentVtxos.Count;
            model.TotalIntentCount = (await pendingIntentsTask).Count;

            // Get receive address for SingleKey wallets
            var wallet = await walletTask;
            if (wallet != null)
            {
                model.WalletType = wallet.WalletType;
                if (wallet.WalletType == WalletType.SingleKey)
                {
                    try
                    {
                        var terms = await clientTransport.GetServerInfoAsync(ct);
                        model.ArkOperatorConnected = terms != null;
                        model.ArkOperatorUrl = arkNetworkConfig.ArkUri;

                        if (terms != null)
                        {
                            var descriptor = OutputDescriptor.Parse(wallet.AccountDescriptor, terms.Network);
                            var defaultContract = new ArkPaymentContract(terms.SignerKey, terms.UnilateralExit, descriptor);
                            model.DefaultAddress = defaultContract.GetArkAddress()
                                .ToString(terms.Network.ChainName == NBitcoin.ChainName.Mainnet);
                        }
                    }
                    catch (Exception ex)
                    {
                        model.ArkOperatorConnected = false;
                        model.ArkOperatorError = ex.Message;
                        model.ArkOperatorUrl = arkNetworkConfig.ArkUri;
                    }
                }
                else
                {
                    // HD wallet — still check operator connection
                    model.ArkOperatorUrl = arkNetworkConfig.ArkUri;
                    try
                    {
                        var terms = await clientTransport.GetServerInfoAsync(ct);
                        model.ArkOperatorConnected = terms != null;
                    }
                    catch (Exception ex)
                    {
                        model.ArkOperatorConnected = false;
                        model.ArkOperatorError = ex.Message;
                    }
                }
            }
            else
            {
                model.ArkOperatorUrl = arkNetworkConfig.ArkUri;
                try
                {
                    var terms = await clientTransport.GetServerInfoAsync(ct);
                    model.ArkOperatorConnected = terms != null;
                }
                catch (Exception ex)
                {
                    model.ArkOperatorConnected = false;
                    model.ArkOperatorError = ex.Message;
                }
            }

            // Get Boltz connection status
            if (boltzClient != null)
            {
                model.BoltzUrl = arkNetworkConfig.BoltzUri;
                try
                {
                    if (boltzLimitsValidator != null)
                    {
                        var limits = await boltzLimitsValidator.GetAllLimitsAsync();
                        model.BoltzConnected = limits != null;
                    }
                }
                catch (Exception ex)
                {
                    model.BoltzConnected = false;
                    model.BoltzError = ex.Message;
                }
            }

            return View(model);
        }
        catch
        {
            return Content(string.Empty);
        }
    }
}
