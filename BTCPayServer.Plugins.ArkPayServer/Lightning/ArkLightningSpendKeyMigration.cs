using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>
/// Backfills store identity for matching wallets and missing spend capabilities for owning stores.
/// Existing capabilities and explicit store bindings are preserved.
/// </summary>
public class ArkLightningSpendKeyMigration(
    StoreRepository storeRepository,
    PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
    ArkLightningSpendKeyService spendKeyService,
    ILogger<ArkLightningSpendKeyMigration> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var lightningPaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");
            var backfilled = 0;
            var leftReceiveOnly = 0;

            foreach (var store in await storeRepository.GetStores())
            {
                stoppingToken.ThrowIfCancellationRequested();

                var lnConfig = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(
                    lightningPaymentMethodId, paymentMethodHandlerDictionary);

                var connectionString = lnConfig?.ConnectionString;
                if (connectionString?.StartsWith("type=arkade", StringComparison.InvariantCultureIgnoreCase) is not true)
                    continue;
                var arkConfig = store.GetPaymentMethodConfig<ArkadePaymentMethodConfig>(
                    ArkadePlugin.ArkadePaymentMethodId, paymentMethodHandlerDictionary);
                if (arkConfig?.WalletId is null)
                    continue;

                var updated = await spendKeyService.BackfillConnectionStringAsync(
                    connectionString, arkConfig.WalletId, store.Id, arkConfig.GeneratedByStore, stoppingToken);
                if (updated is null || updated == connectionString)
                    continue;
                if (!arkConfig.GeneratedByStore && !updated.Contains("spend-key=", StringComparison.OrdinalIgnoreCase))
                    leftReceiveOnly++;
                lnConfig!.ConnectionString = updated;
                store.SetPaymentMethodConfig(
                    paymentMethodHandlerDictionary[lightningPaymentMethodId], lnConfig);
                await storeRepository.UpdateStore(store);
                backfilled++;
            }

            if (backfilled > 0 || leftReceiveOnly > 0)
                logger.LogInformation(
                    "Arkade Lightning connection backfill complete: {Backfilled} store(s) " +
                    "updated, {ReceiveOnly} left receive-only.", backfilled, leftReceiveOnly);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down.
        }
        catch (Exception)
        {
            logger.LogError("Arkade Lightning connection backfill failed.");
        }
    }
}
