using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Notifications;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Notifications;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Wallets;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Bridges the SDK's <see cref="IDestinationSafetyNotifier.DestinationDisabled"/> event to a BTCPay
/// bell notification (<see cref="ArkadeDestinationDisabledNotification"/>) pushed to every store
/// that is configured with the affected Arkade wallet. Failures are caught and logged so a
/// notification error never crashes the host.
/// </summary>
public class DestinationDisabledNotifierBridge(
    IDestinationSafetyNotifier notifier,
    NotificationSender notificationSender,
    StoreRepository storeRepository,
    PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
    ILogger<DestinationDisabledNotifierBridge> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Safe against the reconciliation service's own startup pass: that pass only enqueues a job, and
        // the DestinationDisabled event fires later from a background worker (after a GetServerInfoAsync
        // round-trip), so this synchronous subscription is always in place first. The pending-confirmation
        // flag also persists in wallet Metadata, so the overview banner surfaces the state regardless.
        notifier.DestinationDisabled += OnDisabled;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        notifier.DestinationDisabled -= OnDisabled;
        return Task.CompletedTask;
    }

    private void OnDisabled(object? sender, DestinationDisabledEventArgs e) => _ = HandleAsync(e);

    private async Task HandleAsync(DestinationDisabledEventArgs e)
    {
        try
        {
            var storeIds = await StoreIdsForWallet(e.WalletId);
            foreach (var storeId in storeIds)
            {
                await notificationSender.SendNotification(
                    new StoreScope(storeId),
                    new ArkadeDestinationDisabledNotification { StoreId = storeId });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to push Arkade destination-disabled notification for wallet {WalletId}",
                e.WalletId);
        }
    }

    private async Task<List<string>> StoreIdsForWallet(string walletId)
    {
        StoreData[] allStores = await storeRepository.GetStores();
        var result = new List<string>();
        foreach (var store in allStores)
        {
            var config = store.GetPaymentMethodConfig<ArkadePaymentMethodConfig>(
                ArkadePlugin.ArkadePaymentMethodId,
                paymentMethodHandlerDictionary);
            if (config?.WalletId == walletId)
                result.Add(store.Id);
        }
        return result;
    }
}
