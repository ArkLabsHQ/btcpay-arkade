using System.Threading.Channels;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Lightning.Events;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.Models.Events;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk;
using NArk.Extensions;
using NArk.Services.Abstractions;
using NBitcoin;
using NBXplorer;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkContractInvoiceListener(
    IMemoryCache memoryCache,
    InvoiceRepository invoiceRepository,
    ArkadePaymentMethodHandler arkadePaymentMethodHandler,
    IOperatorTermsService operatorTermsService,
    EventAggregator eventAggregator,
    ArkWalletService arkWalletService,
    PaymentService paymentService,
    ILogger<ArkContractInvoiceListener> logger)
    : IHostedService
{
    private readonly Channel<string> _checkInvoices = Channel.CreateUnbounded<string>();
    private CompositeDisposable _leases = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await QueueMonitoredInvoices(cancellationToken);
        _leases.Add(eventAggregator.SubscribeAsync<InvoiceEvent>(OnInvoiceEvent));
        _leases.Add(eventAggregator.SubscribeAsync<VTXOsUpdated>(OnVTXOs));
        _leases.Add(eventAggregator.SubscribeAsync<ArkSwapUpdated>(HandleSwapUpdate));

        _ = PollAllInvoices(cancellationToken);
    }

    private async Task HandleSwapUpdate(ArkSwapUpdated lightningSwapUpdated)
    {
        var terms = await operatorTermsService.GetOperatorTerms();
        var active = ArkLightningClient.Map(lightningSwapUpdated.Swap, terms.Network)
            .Status == LightningInvoiceStatus.Unpaid;
        await arkWalletService.ToggleContract(lightningSwapUpdated.Swap.WalletId, lightningSwapUpdated.Swap.ContractScript,
            active);
    }

    private async Task OnInvoiceEvent(InvoiceEvent invoiceEvent)
    {
        memoryCache.Remove(GetCacheKey(invoiceEvent.Invoice.Id));
        _checkInvoices.Writer.TryWrite(invoiceEvent.Invoice.Id);
    }

    private async Task OnVTXOs(VTXOsUpdated arg)
    {
        var terms = await operatorTermsService.GetOperatorTerms();
        foreach (var scriptVtxos in arg.Vtxos.GroupBy(c => c.Script))
        {
           var script = Script.FromHex(scriptVtxos.Key);
            var address = ArkAddress.FromScriptPubKey(script, terms.SignerKey.ToXOnlyPubKey());
            var network = terms.Network;
            var inv = await invoiceRepository.GetInvoiceFromAddress(ArkadePlugin.ArkadePaymentMethodId, address.ToString(network.ChainName == ChainName.Mainnet)); 
            if (inv is null)
                continue;
            foreach (var vtxo in scriptVtxos)
            {
                await HandlePaymentData(vtxo, inv, arkadePaymentMethodHandler);
            }
        }
    }

    private Task ReceivedPayment(InvoiceEntity invoice, PaymentEntity payment)
    {
        logger.LogInformation("Invoice {invoiceId} received payment {amount} {currency} {paymentId}",
            invoice.Id, payment.Value, payment.Currency, payment.Id);

        eventAggregator.Publish(
            new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
        return Task.CompletedTask;
    }
    
    private async Task HandlePaymentData(VTXO vtxo, InvoiceEntity invoice, ArkadePaymentMethodHandler handler)
    {
        var pmi = ArkadePlugin.ArkadePaymentMethodId;
        var details = new ArkadePaymentData($"{vtxo.TransactionId}:{vtxo.TransactionOutputIndex}");
        var paymentData = new PaymentData
        {
            Status = PaymentStatus.Settled,
            Amount = Money.Satoshis(vtxo.Amount).ToDecimal(MoneyUnit.BTC),
            Created = vtxo.SeenAt,
            Id = details.Outpoint,
            Currency = "BTC",
        }.Set(invoice, handler, details);


        var alreadyExistingPaymentThatMatches = invoice
            .GetPayments(false)
            .SingleOrDefault(c => c.Id == paymentData.Id && c.PaymentMethodId == pmi);

        if (alreadyExistingPaymentThatMatches == null)
        {
            var payment = await paymentService.AddPayment(paymentData);
            if (payment != null)
            {
                await ReceivedPayment(invoice, payment);
            }
        }
        else
        {
            //else update it with the new data
            alreadyExistingPaymentThatMatches.Status = PaymentStatus.Settled;
            alreadyExistingPaymentThatMatches.Details = JToken.FromObject(details, handler.Serializer);
            await paymentService.UpdatePayments([alreadyExistingPaymentThatMatches]);
        }

        eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
    }
    
    
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _leases.Dispose();
        _leases = new CompositeDisposable();
        return Task.CompletedTask;
    }

    public async Task ToggleArkadeContract(InvoiceEntity invoice)
    {
        var active = invoice.Status == InvoiceStatus.New;
        var listenedContract = GetListenedArkadeInvoice(invoice);
        if (listenedContract is null)
        {
            return;
        }

        await arkWalletService.ToggleContract(listenedContract.Details.WalletId, listenedContract.Details.Contract,
            active);
    }

    private ArkadeListenedContract? GetListenedArkadeInvoice(InvoiceEntity invoice)
    {
        var prompt = invoice.GetPaymentPrompt(ArkadePlugin.ArkadePaymentMethodId);
        return prompt is null
            ? null
            : new ArkadeListenedContract(

                arkadePaymentMethodHandler.ParsePaymentPromptDetails(prompt.Details),
                invoice.Id
            );
    }

    private static DateTimeOffset GetExpiration(InvoiceEntity invoice)
    {
        var expiredIn = DateTimeOffset.UtcNow - invoice.ExpirationTime;
        return DateTimeOffset.UtcNow + (expiredIn >= TimeSpan.FromMinutes(5.0) ? expiredIn : TimeSpan.FromMinutes(5.0));
    }

    private string GetCacheKey(string invoiceId)
    {
        return $"{nameof(GetListenedArkadeInvoice)}-{invoiceId}";
    }

    private Task<InvoiceEntity> GetInvoice(string invoiceId)
    {
        return memoryCache.GetOrCreateAsync(GetCacheKey(invoiceId), async cacheEntry =>
        {
            var invoice = await invoiceRepository.GetInvoice(invoiceId);
            if (invoice is null)
                return null;
            cacheEntry.AbsoluteExpiration = GetExpiration(invoice);
            return invoice;
        })!;
    }


    private async Task QueueMonitoredInvoices(CancellationToken cancellation)
    {
        foreach (var invoice in await invoiceRepository.GetMonitoredInvoices(ArkadePlugin.ArkadePaymentMethodId,
                     cancellation))
        {
            if (GetListenedArkadeInvoice(invoice) is null) continue;
            _checkInvoices.Writer.TryWrite(invoice.Id);
            memoryCache.Set(GetCacheKey(invoice.Id), invoice, GetExpiration(invoice));
        }

    }

    private async Task PollAllInvoices(CancellationToken cancellation)
    {
        retry:
        if (cancellation.IsCancellationRequested)
            return;
        try
        {
            await foreach (var invoiceId in _checkInvoices.Reader.ReadAllAsync(cancellation))
            {
                logger.LogInformation("Checking for invoice {InvoiceId}", invoiceId);
                var invoice = await GetInvoice(invoiceId);
                await ToggleArkadeContract(invoice);
            }
        }
        catch when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await Task.Delay(1000, cancellation);
            logger.LogWarning(ex, "Unhandled error in the Arkade invoice listener.");
            goto retry;
        }
        
        logger.LogInformation("Exiting poll loop.");
    }
}