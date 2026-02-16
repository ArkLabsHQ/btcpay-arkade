using BTCPayServer.Data;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Plugins.ArkPayServer.Helpers;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Mvc;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Hosting;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.ViewComponents;

public class ArkActivityDashboardWidgetViewComponent(
    StoreRepository storeRepository,
    PaymentMethodHandlerDictionary handlerDictionary,
    IIntentStorage intentStorage,
    ISwapStorage swapStorage,
    IVtxoStorage vtxoStorage,
    ArkNetworkConfig arkNetworkConfig) : ViewComponent
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

            var intentsTask = intentStorage.GetIntents(
                walletIds: [walletId], take: 5, states: [ ArkIntentState.BatchInProgress, ArkIntentState.BatchSucceeded, ArkIntentState.WaitingForBatch, ArkIntentState.WaitingToSubmit], cancellationToken: ct);

            var swapsTask = swapStorage.GetSwaps(
                walletIds: [walletId], take: 5,  status: [ArkSwapStatus.Pending ,ArkSwapStatus.Settled],cancellationToken: ct);

            var vtxosTask = vtxoStorage.GetVtxos(
                walletIds: [walletId], take: 5, cancellationToken: ct);

            await Task.WhenAll(intentsTask, swapsTask, vtxosTask);

            var items = new List<ActivityItem>();

            foreach (var intent in await intentsTask)
            {
                var (statusClass, statusText) = intent.State switch
                {
                    ArkIntentState.WaitingToSubmit => ("text-bg-secondary", "Waiting"),
                    ArkIntentState.WaitingForBatch => ("text-bg-warning", "Queued"),
                    ArkIntentState.BatchInProgress => ("text-bg-primary", "In Progress"),
                    ArkIntentState.BatchSucceeded => ("text-bg-success", "Settled"),
                    ArkIntentState.BatchFailed => ("text-bg-danger", "Failed"),
                    ArkIntentState.Cancelled => ("text-bg-dark", "Cancelled"),
                    _ => ("text-bg-secondary", intent.State.ToString())
                };

                items.Add(new ActivityItem
                {
                    Date = intent.CreatedAt,
                    Type = ActivityItemType.Batch,
                    Label = !string.IsNullOrEmpty(intent.CommitmentTransactionId) ? intent.CommitmentTransactionId : intent.IntentTxId,
                    Link =  !string.IsNullOrEmpty(intent.CommitmentTransactionId) ? ArkadeLinkHelper.GetCommitmentTransactionLink(arkNetworkConfig,intent.CommitmentTransactionId) : null,
                    StatusClass = statusClass,
                    StatusText = statusText
                });
            }

            foreach (var swap in await swapsTask)
            {
                var (statusClass, statusText) = swap.Status switch
                {
                    ArkSwapStatus.Pending => ("text-bg-info", "Pending"),
                    ArkSwapStatus.Settled => ("text-bg-success", "Settled"),
                    ArkSwapStatus.Failed => ("text-bg-danger", "Failed"),
                    _ => ("text-bg-warning", swap.Status.ToString())
                };

                items.Add(new ActivityItem
                {
                    Date = swap.CreatedAt,
                    Type = ActivityItemType.Swap,
                    Label = swap.SwapId,
                    StatusClass = statusClass,
                    StatusText = statusText,
                    Link = ArkadeLinkHelper.GetSwapLink(arkNetworkConfig, swap.SwapId),
                    Amount = $"{Money.Satoshis(swap.ExpectedAmount).ToDecimal(MoneyUnit.BTC)} BTC"
                });
            }

            foreach (var vtxo in await vtxosTask)
            {
                var spent = vtxo.IsSpent();

                items.Add(new ActivityItem
                {
                    Date = vtxo.CreatedAt,
                    Type = ActivityItemType.Vtxo,
                    Label = ArkadeLinkHelper.FormatOutpoint(vtxo.TransactionId, vtxo.TransactionOutputIndex),
                    Link = ArkadeLinkHelper.GetOutpointLink(arkNetworkConfig, vtxo.TransactionId, vtxo.TransactionOutputIndex),
                    StatusClass = spent ? "text-bg-secondary" : "text-bg-success",
                    StatusText = spent ? "Spent" : "Unspent",
                    Amount = $"{Money.Satoshis((long)vtxo.Amount).ToDecimal(MoneyUnit.BTC)} BTC"
                });
            }

            items.Sort((a, b) => b.Date.CompareTo(a.Date));
            if (items.Count > 10)
                items = items.Take(10).ToList();

            var model = new ArkActivityDashboardWidgetViewModel
            {
                StoreId = dashboardModel.StoreId,
                Items = items
            };

            return View(model);
        }
        catch
        {
            return Content(string.Empty);
        }
    }
}
