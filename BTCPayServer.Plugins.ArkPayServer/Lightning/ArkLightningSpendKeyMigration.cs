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
/// Backfills the spend capability into Arkade Lightning connection strings written before
/// capabilities existed, so stores configured earlier keep working.
///
/// A store is backfilled only when it owns the wallet it is configured against. Stores
/// configured against a wallet they do not own are left receive-only.
///
/// Runs in the background rather than in <c>StartAsync</c> so a slow or failing pass never
/// blocks host startup.
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
                if (connectionString.Contains("spend-key=", StringComparison.InvariantCultureIgnoreCase))
                    continue;

                var arkConfig = store.GetPaymentMethodConfig<ArkadePaymentMethodConfig>(
                    ArkadePlugin.ArkadePaymentMethodId, paymentMethodHandlerDictionary);
                if (arkConfig?.WalletId is null)
                    continue;

                if (!arkConfig.GeneratedByStore)
                {
                    leftReceiveOnly++;
                    continue;
                }

                lnConfig!.ConnectionString = await spendKeyService.BuildConnectionStringAsync(
                    arkConfig.WalletId, stoppingToken);
                store.SetPaymentMethodConfig(
                    paymentMethodHandlerDictionary[lightningPaymentMethodId], lnConfig);
                await storeRepository.UpdateStore(store);
                backfilled++;
            }

            if (backfilled > 0 || leftReceiveOnly > 0)
                logger.LogInformation(
                    "Arkade Lightning spend capability backfill complete: {Backfilled} store(s) " +
                    "updated, {ReceiveOnly} left receive-only.", backfilled, leftReceiveOnly);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Arkade Lightning spend capability backfill failed.");
        }
    }
}
