using System.Threading.Channels;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.VTXOs;
using NArk.Swaps.Abstractions;
using NArk.Core.Transport;
using NBitcoin;
using NBXplorer;
using Newtonsoft.Json.Linq;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Swaps.Services;
using NArk.Storage.EfCore.Entities;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkContractInvoiceListener(
    IMemoryCache memoryCache,
    InvoiceRepository invoiceRepository,
    ArkadePaymentMethodHandler arkadePaymentMethodHandler,
    ArkadeAssetPaymentMethodHandler arkadeAssetPaymentMethodHandler,
    IClientTransport clientTransport,
    EventAggregator eventAggregator,
    IContractStorage contractStorage,
    PaymentService paymentService,
    IVtxoStorage vtxoStorage,
    ISwapStorage swapStorage,
    ILogger<ArkContractInvoiceListener> logger)
    : IHostedService
{
    private readonly Channel<string> _checkInvoices = Channel.CreateUnbounded<string>();
    private readonly SemaphoreSlim _paymentLock = new(1, 1);
    private CompositeDisposable _leases = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await QueueMonitoredInvoices(cancellationToken);
        _leases.Add(eventAggregator.SubscribeAsync<InvoiceEvent>(OnInvoiceEvent));

        // Subscribe to NNark's storage events directly
        vtxoStorage.VtxosChanged += OnVtxoChanged;
        swapStorage.SwapsChanged += OnSwapChanged;


        _ = PollAllInvoices(cancellationToken);
    }

    private async void OnSwapChanged(object? sender, NArk.Swaps.Models.ArkSwap swap)
    {
        try
        {
            // Only process reverse submarine swaps (Lightning -> Ark)
            if (swap.SwapType != NArk.Swaps.Models.ArkSwapType.ReverseSubmarine)
                return;

            var activityState = swap.Status == NArk.Swaps.Models.ArkSwapStatus.Pending
                ? ContractActivityState.Active
                : ContractActivityState.Inactive;
            await contractStorage.UpdateContractActivityState(swap.WalletId, swap.ContractScript, activityState);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling swap change for {SwapId}", swap.SwapId);
        }
    }

    private async Task OnInvoiceEvent(InvoiceEvent invoiceEvent)
    {
        memoryCache.Remove(GetCacheKey(invoiceEvent.Invoice.Id));
        _checkInvoices.Writer.TryWrite(invoiceEvent.Invoice.Id);
    }

    private async void OnVtxoChanged(object? sender, ArkVtxo vtxo)
    {
        try
        {
            // Note: Auto-deactivation of AwaitingFundsBeforeDeactivate contracts is now handled
            // by VtxoSynchronizationService in NNark library

            var terms = await clientTransport.GetServerInfoAsync();
            var network = terms.Network;
            var script = Script.FromHex(vtxo.Script);

            // Try to find the invoice by address — handle both Ark and boarding contracts
            InvoiceEntity? inv = null;
            string? paymentDestination = null;

            // Check if this is a boarding contract (P2TR on-chain address)
            var contracts = await contractStorage.GetContracts(
                scripts: [vtxo.Script],
                contractTypes: [NArk.Core.Contracts.ArkBoardingContract.ContractType],
                cancellationToken: CancellationToken.None);

            var isBoarding = contracts.Count > 0;
            if (isBoarding)
            {
                // Boarding VTXO: look up invoice by P2TR Bitcoin address
                var btcAddress = script.GetDestinationAddress(network);
                if (btcAddress is not null)
                {
                    paymentDestination = btcAddress.ToString();
                    inv = await invoiceRepository.GetInvoiceFromAddress(
                        ArkadePlugin.ArkadePaymentMethodId, paymentDestination);
                }
            }
            else
            {
                // Standard Ark VTXO: look up invoice by Ark address
                var serverKey = terms.SignerKey.Extract().XOnlyPubKey;
                var address = ArkAddress.FromScriptPubKey(script, serverKey);
                paymentDestination = address.ToString(network.ChainName == ChainName.Mainnet);
                inv = await invoiceRepository.GetInvoiceFromAddress(
                    ArkadePlugin.ArkadePaymentMethodId, paymentDestination);

                // The dedicated Arkade Asset method derives its own address, so a
                // VTXO arriving there matches no ARKADE invoice. Try the asset
                // method and, if the arriving asset is one this invoice offered,
                // settle that instead.
                if (inv is null)
                {
                    var assetInvoice = await invoiceRepository.GetInvoiceFromAddress(
                        ArkadePlugin.ArkadeAssetPaymentMethodId, paymentDestination);
                    if (assetInvoice is not null)
                    {
                        await HandleAssetPayment(assetInvoice, vtxo, paymentDestination);
                        return;
                    }
                }
            }

            if (inv is null)
                return;

            // Boarding payments: Processing until confirmed, then Settled
            var isConfirmed = !isBoarding || vtxo.Metadata?.GetValueOrDefault("Confirmed") == "True";

            // Map NNark's ArkVtxo to plugin's VtxoEntity entity
            var vtxoEntity = new VtxoEntity
            {
                TransactionId = vtxo.TransactionId,
                TransactionOutputIndex = (int)vtxo.TransactionOutputIndex,
                Amount = (long)vtxo.Amount,
                Script = vtxo.Script,
                SeenAt = vtxo.CreatedAt
            };
            await HandlePaymentData(vtxoEntity, inv, arkadePaymentMethodHandler,
                ArkadePlugin.ArkadePaymentMethodId, paymentDestination, isConfirmed, isBoarding);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling VTXO change for {TxId}:{Index}", vtxo.TransactionId, vtxo.TransactionOutputIndex);
        }
    }

    /// <summary>
    /// Settles a payment on the dedicated Arkade Asset method. The invoice offered
    /// one option per enabled asset (each a fixed base-unit amount due); match the
    /// arriving VTXO's assets against those options by asset id and credit BTC in
    /// proportion to the asset received, so BTCPay's accounting (partial / settled
    /// / overpaid) stays correct. A VTXO carrying none of the offered assets is
    /// ignored — it can't settle the invoice.
    /// </summary>
    private async Task HandleAssetPayment(InvoiceEntity invoice, ArkVtxo vtxo, string destination)
    {
        var prompt = invoice.GetPaymentPrompt(ArkadePlugin.ArkadeAssetPaymentMethodId);
        if (prompt?.Details is null)
            return;
        var details = arkadeAssetPaymentMethodHandler.ParsePaymentPromptDetails(prompt.Details);

        // Match strictly by asset id against the offered options. arkd returns
        // asset ids as lowercase hex; the option stores the merchant-entered id,
        // so compare case-insensitively.
        ArkadeAssetOption? matched = null;
        ulong received = 0UL;
        foreach (var option in details.Options)
        {
            var amount = vtxo.Assets?
                .Where(a => string.Equals(a.AssetId, option.AssetId, StringComparison.OrdinalIgnoreCase))
                .Aggregate(0UL, (sum, a) => sum + a.Amount) ?? 0UL;
            if (amount > 0UL)
            {
                matched = option;
                received = amount;
                break;
            }
        }
        if (matched is null || matched.BaseUnitsDue == 0UL)
            return; // no offered asset arrived on this VTXO

        // Credit BTC strictly proportional to the asset received — NOT capped at
        // 100%. An over-payment in the asset must surface as an over-payment in
        // BTCPay's books (refund / reconciliation depend on it). TotalDue is fixed
        // at invoice creation, so reading it from the pre-lock prompt snapshot is safe.
        var ratio = (decimal)received / matched.BaseUnitsDue;
        var creditBtc = prompt.Calculate().TotalDue * ratio;
        logger.LogInformation(
            "Invoice {invoiceId}: Arkade asset {assetId} received {received} (due {due}) → crediting {btc} BTC",
            invoice.Id, matched.AssetId, received, matched.BaseUnitsDue, creditBtc);

        // Asset VTXOs are off-chain Arkade VTXOs (never boarding) → confirmed on arrival.
        var vtxoEntity = new VtxoEntity
        {
            TransactionId = vtxo.TransactionId,
            TransactionOutputIndex = (int)vtxo.TransactionOutputIndex,
            Amount = (long)vtxo.Amount,
            Script = vtxo.Script,
            SeenAt = vtxo.CreatedAt
        };
        await HandlePaymentData(vtxoEntity, invoice, arkadeAssetPaymentMethodHandler,
            ArkadePlugin.ArkadeAssetPaymentMethodId, destination, isConfirmed: true, isBoarding: false,
            amountOverrideBtc: creditBtc);
    }

    private Task ReceivedPayment(InvoiceEntity invoice, PaymentEntity payment)
    {
        logger.LogInformation("Invoice {invoiceId} received payment {amount} {currency} {paymentId}",
            invoice.Id, payment.Value, payment.Currency, payment.Id);

        eventAggregator.Publish(
            new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
        return Task.CompletedTask;
    }
    
    private async Task HandlePaymentData(VtxoEntity vtxo, InvoiceEntity invoice, IPaymentMethodHandler handler, PaymentMethodId pmi, string? destination = null, bool isConfirmed = true, bool isBoarding = false, decimal? amountOverrideBtc = null)
    {
        var details = new ArkadePaymentData($"{vtxo.TransactionId}:{vtxo.TransactionOutputIndex}", destination, isBoarding);
        var status = isConfirmed ? PaymentStatus.Settled : PaymentStatus.Processing;

        // Serialize payment registration to prevent duplicate inserts from concurrent VTXO events
        await _paymentLock.WaitAsync();
        try
        {
            // Re-fetch the invoice inside the lock to get the latest payment state
            var freshInvoice = await invoiceRepository.GetInvoice(invoice.Id);
            if (freshInvoice is null)
                return;

            var paymentData = new PaymentData
            {
                Status = status,
                Amount = amountOverrideBtc ?? Money.Satoshis(vtxo.Amount).ToDecimal(MoneyUnit.BTC),
                Created = vtxo.SeenAt,
                Id = details.Outpoint,
                Currency = "BTC",
            }.Set(freshInvoice, handler, details);

            // Override destination if payment came via boarding address (not the Ark contract address)
            //
            // The PaymentBlob is serialised through BTCPay's default camelCase resolver, so
            // the on-disk JSON property is lowercase `destination` even though the C# field
            // is `Destination`. JObject's indexer is case-sensitive — writing `blob["Destination"]`
            // adds a sibling field instead of replacing the lowercase one, and the deserialiser
            // then ignores our shadow value. Update the lowercase key (and remove any stale
            // capital-D shadow left by earlier versions of this code).
            if (destination is not null)
            {
                var blob = JObject.Parse(paymentData.Blob2);
                blob["destination"] = destination;
                blob.Remove("Destination");
                paymentData.Blob2 = blob.ToString(Newtonsoft.Json.Formatting.None);
            }

            var alreadyExistingPaymentThatMatches = freshInvoice
                .GetPayments(false)
                .SingleOrDefault(c => c.Id == paymentData.Id && c.PaymentMethodId == pmi);

            if (alreadyExistingPaymentThatMatches == null)
            {
                var payment = await paymentService.AddPayment(paymentData);
                if (payment != null)
                {
                    await ReceivedPayment(freshInvoice, payment);
                }
            }
            else
            {
                // Update existing payment — upgrade Processing→Settled on confirmation
                alreadyExistingPaymentThatMatches.Status = status;
                alreadyExistingPaymentThatMatches.Details = JToken.FromObject(details, handler.Serializer);
                await paymentService.UpdatePayments([alreadyExistingPaymentThatMatches]);
            }
        }
        finally
        {
            _paymentLock.Release();
        }

        eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
    }
    
    
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        vtxoStorage.VtxosChanged -= OnVtxoChanged;
        swapStorage.SwapsChanged -= OnSwapChanged;
        _leases.Dispose();
        _leases = new CompositeDisposable();
    }

    public async Task ToggleArkadeContract(InvoiceEntity invoice)
    {
        var activityState = invoice.Status == InvoiceStatus.New
            ? ContractActivityState.Active
            : ContractActivityState.Inactive;
        var walletId = GetArkadeInvoiceWalletId(invoice);
        if (walletId is null)
        {
            return;
        }

        // ConfigurePrompt tags every contract it derives for this invoice with
        // Source = "invoice:{id}": the BTC-VTXO Payment contract, the Boarding
        // contract (when enabled), and the Arkade Asset method's receive
        // contract. Find every contract carrying this invoice's source tag and
        // toggle them all. HTLC contracts use a different "swap:{id}" Source tag
        // and are driven by OnSwapChanged based on swap state, not invoice state.
        var invoiceSource = $"invoice:{invoice.Id}";
        var contracts = await contractStorage.GetContracts(
            walletIds: [walletId],
            cancellationToken: CancellationToken.None);
        foreach (var c in contracts.Where(c => c.Metadata?.GetValueOrDefault("Source") == invoiceSource))
        {
            await contractStorage.UpdateContractActivityState(walletId, c.Script, activityState);
        }
    }

    private ArkadeListenedContract? GetListenedArkadeInvoice(InvoiceEntity invoice)
    {
        var prompt = invoice.GetPaymentPrompt(ArkadePlugin.ArkadePaymentMethodId);
        if (prompt?.Details is null)
            return null;

        return new ArkadeListenedContract(
            arkadePaymentMethodHandler.ParsePaymentPromptDetails(prompt.Details),
            invoice.Id);
    }

    /// <summary>
    /// Resolves the Arkade wallet id backing an invoice from whichever Arkade
    /// prompt has been activated — the BTC-VTXO method or the dedicated asset
    /// method. Both derive their contracts from the same wallet and tag them
    /// Source = "invoice:{id}", so either id is sufficient to find and toggle
    /// every contract for the invoice.
    /// </summary>
    private string? GetArkadeInvoiceWalletId(InvoiceEntity invoice)
    {
        var arkPrompt = invoice.GetPaymentPrompt(ArkadePlugin.ArkadePaymentMethodId);
        if (arkPrompt?.Details is not null)
            return arkadePaymentMethodHandler.ParsePaymentPromptDetails(arkPrompt.Details).WalletId;

        var assetPrompt = invoice.GetPaymentPrompt(ArkadePlugin.ArkadeAssetPaymentMethodId);
        if (assetPrompt?.Details is not null)
            return arkadeAssetPaymentMethodHandler.ParsePaymentPromptDetails(assetPrompt.Details).WalletId;

        return null;
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