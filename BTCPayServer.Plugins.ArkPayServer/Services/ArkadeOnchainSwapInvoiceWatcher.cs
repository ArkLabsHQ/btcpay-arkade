using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Blockchain;
using NArk.ArkadeIntents;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Keeps an invoice open while its onchain swap is being paid. BTCPay does not watch the swap's HTLC,
/// so without this the invoice expires under a payer who paid on time, just on L1.
/// </summary>
public class ArkadeOnchainSwapInvoiceWatcher(
    InvoiceRepository invoiceRepository,
    ArkadePaymentMethodHandler handler,
    IBitcoinBlockchain blockchain,
    ILogger<ArkadeOnchainSwapInvoiceWatcher> logger,
    IArkadeIntentStorage? intentStorage = null) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (intentStorage is null) return;

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Checking onchain swap invoices failed; retrying next tick");
            }
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        var invoices = await invoiceRepository.GetMonitoredInvoices(ArkadePlugin.ArkadePaymentMethodId, cancellationToken);
        foreach (var invoice in invoices.Where(i => i.Status == InvoiceStatus.New))
        {
            if (invoice.GetPaymentPrompt(ArkadePlugin.ArkadePaymentMethodId)?.Details is not { } raw
                || handler.ParsePaymentPromptDetails(raw) is not { SwapHtlcAddress: { } htlc, SwapId: { } swapId })
                continue;

            if (await intentStorage!.GetArkadeSwapIntent(swapId, cancellationToken) is not { RefundLocktime: { } deadline }
                || OnchainSwapInvoicePolicy.ExtensionFor(invoice.ExpirationTime, DateTimeOffset.UtcNow, deadline) is not { } extension)
                continue;

            if ((await blockchain.GetUtxosAsync(htlc, cancellationToken)).Count == 0)
                continue;

            await invoiceRepository.UpdateInvoiceExpiry(invoice.Id, extension);
            logger.LogInformation(
                "Invoice {InvoiceId}: swap HTLC {Htlc} is funded, keeping the invoice open for {Minutes:F0} min",
                invoice.Id, htlc, extension.TotalMinutes);
        }
    }
}
