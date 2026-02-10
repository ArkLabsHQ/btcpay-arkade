using BTCPayServer.Data;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Plugins.Arkade.Controllers;
using BTCPayServer.Plugins.Arkade.Models;
using BTCPayServer.Plugins.Arkade.PaymentHandler;
using BTCPayServer.Plugins.Arkade.Lightning;
using BTCPayServer.Plugins.Arkade.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Mvc;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Core.Transport;
using NArk.Hosting;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;

namespace BTCPayServer.Plugins.Arkade.ViewComponents;

/// <summary>
/// ViewComponent for the Arkade wallet widget on the BTCPay dashboard.
/// Shows wallet balance summary and service connection status using the same
/// partials as the StoreOverview page.
/// </summary>
public class ArkDashboardWidgetViewComponent : ViewComponent
{
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlerDictionary;
    private readonly IClientTransport _clientTransport;
    private readonly ArkNetworkConfig _arkNetworkConfig;
    private readonly ArkController _arkController;
    private readonly BoltzClient? _boltzClient;
    private readonly BoltzLimitsValidator? _boltzLimitsValidator;

    public ArkDashboardWidgetViewComponent(
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlerDictionary,
        IClientTransport clientTransport,
        ArkNetworkConfig arkNetworkConfig,
        ArkController arkController, 
        BoltzClient? boltzClient = null,
        BoltzLimitsValidator? boltzLimitsValidator = null
        )
    {
        _storeRepository = storeRepository;
        _handlerDictionary = handlerDictionary;
        _clientTransport = clientTransport;
        _arkNetworkConfig = arkNetworkConfig;
        _arkController = arkController;
        _boltzClient = boltzClient;
        _boltzLimitsValidator = boltzLimitsValidator;
    }

    public async Task<IViewComponentResult> InvokeAsync(StoreDashboardViewModel dashboardModel)
    {
        if (string.IsNullOrEmpty(dashboardModel?.StoreId))
        {
            return Content(string.Empty);
        }

        try
        {
            // Get store data and check for Arkade configuration
            var store = await _storeRepository.FindStore(dashboardModel.StoreId);
            if (store == null)
            {
                return Content(string.Empty);
            }

            var config = store.GetPaymentMethodConfig<ArkadePaymentMethodConfig>(
                ArkadePlugin.ArkadePaymentMethodId,
                _handlerDictionary,
                true);

            if (config == null || string.IsNullOrEmpty(config.WalletId))
            {
                return Content(string.Empty);
            }

            // Build StoreOverviewViewModel with the data needed for partials
            var model = new StoreOverviewViewModel
            {
                StoreId = dashboardModel.StoreId,
                WalletId = config.WalletId,
                Balances = await _arkController.GetArkBalances(config.WalletId, HttpContext.RequestAborted)
            };

            // Get Ark operator connection status
            model.ArkOperatorUrl = _arkNetworkConfig.ArkUri;
            try
            {
                var terms = await _clientTransport.GetServerInfoAsync();
                model.ArkOperatorConnected = terms != null;
            }
            catch (Exception ex)
            {
                model.ArkOperatorConnected = false;
                model.ArkOperatorError = ex.Message;
            }

            // Get Boltz connection status if available
            if (_boltzClient != null)
            {
                model.BoltzUrl = _arkNetworkConfig.BoltzUri;
                try
                {
                    // Check if Boltz is connected by checking limits
                    if (_boltzLimitsValidator != null)
                    {
                        var limits = await _boltzLimitsValidator.GetAllLimitsAsync();
                        model.BoltzConnected = limits != null;

                        if (limits != null)
                        {
                            model.BoltzReverseMinAmount = limits.ReverseMinAmount;
                            model.BoltzReverseMaxAmount = limits.ReverseMaxAmount;
                            model.BoltzReverseFeePercentage = limits.ReverseFeePercentage;
                            model.BoltzReverseMinerFee = limits.ReverseMinerFee;

                            model.BoltzSubmarineMinAmount = limits.SubmarineMinAmount;
                            model.BoltzSubmarineMaxAmount = limits.SubmarineMaxAmount;
                            model.BoltzSubmarineFeePercentage = limits.SubmarineFeePercentage;
                            model.BoltzSubmarineMinerFee = limits.SubmarineMinerFee;
                        }
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
            // If anything fails, just don't show the widget
            return Content(string.Empty);
        }
    }
}
