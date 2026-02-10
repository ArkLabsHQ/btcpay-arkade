using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.Payouts;
using BTCPayServer.Plugins.Arkade.Services;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Wallets;
using NArk.Core.Transport;
using NBitcoin;
using PayoutData = BTCPayServer.Data.PayoutData;
using PayoutProcessorData = BTCPayServer.Data.PayoutProcessorData;

namespace BTCPayServer.Plugins.Arkade.Payouts.Ark;

public class ArkAutomatedPayoutProcessor: BaseAutomatedPayoutProcessor<ArkAutomatedPayoutBlob>
{
    private readonly IClientTransport _clientTransport;
    private readonly ArkadeSpendingService _arkSpendingService;
    private readonly PayoutMethodHandlerDictionary _payoutMethodHandlers;
    private readonly BTCPayNetworkJsonSerializerSettings _jsonSerializerSettings;

    public ArkAutomatedPayoutProcessor(
        IClientTransport clientTransport,
        ILoggerFactory logger,
        StoreRepository storeRepository,
        PayoutProcessorData payoutProcessorSettings,
        ApplicationDbContextFactory applicationDbContextFactory,
        PaymentMethodHandlerDictionary paymentHandlers,
        IPluginHookService pluginHookService,
        EventAggregator eventAggregator,
        ArkadeSpendingService arkSpendingService,
        PayoutMethodHandlerDictionary payoutMethodHandlers,
        BTCPayNetworkJsonSerializerSettings jsonSerializerSettings,
        IWalletProvider walletProvider
    ) 
        : base(ArkadePlugin.ArkadePaymentMethodId, logger, storeRepository, payoutProcessorSettings, applicationDbContextFactory, paymentHandlers, pluginHookService, eventAggregator)
    {
        _clientTransport = clientTransport;
        _arkSpendingService = arkSpendingService;
        _payoutMethodHandlers = payoutMethodHandlers;
        _jsonSerializerSettings = jsonSerializerSettings;
    }

    protected override async Task Process(object paymentMethodConfig, List<PayoutData> payouts)
    {
        var payoutHandler = (ArkPayoutHandler)_payoutMethodHandlers[ArkadePlugin.ArkadePayoutMethodId];
        
        var terms = await _clientTransport.GetServerInfoAsync();

        var storeData = await _storeRepository.FindStore(PayoutProcessorSettings.StoreId) ??
            throw new InvalidOperationException("Could not find store by StoreId");
        
        foreach (var payout in payouts)
        {
            if (payoutHandler.PayoutLocker.LockOrNullAsync(payout.Id, 0) is { } locker && await locker is {} disposable)
            {
                using (disposable)
                {
                    
                    var amount = new Money(payout.Amount.Value, MoneyUnit.BTC);
            
                    if (amount < terms.Dust)
                        payout.State = PayoutState.Cancelled;

                    if (payout.GetPayoutMethodId() != PayoutMethodId)
                        continue;

                    if (payout.Proof is not null)
                        continue;
            
                    var blob = payout.GetBlob(_jsonSerializerSettings);
                    var claim = await payoutHandler.ParseClaimDestination(blob.Destination, CancellationToken.None);
                    var destinationBip21 = await payoutHandler.TryGenerateBip21(payout, claim);

                    if (destinationBip21 is not null)
                    {
                        try
                        {
                            var txId = await _arkSpendingService.Spend(storeData, destinationBip21, CancellationToken.None);
                    
                            payoutHandler.SetProofBlob(payout, new ArkPayoutProof { TransactionId = uint256.Parse(txId) });
                            if(!string.IsNullOrEmpty(txId ))
                                payout.State = PayoutState.Completed;
                        }
                        catch (Exception e)
                        {
                        }
                
                    }
                    else
                        payout.State = PayoutState.Cancelled;
                }
            }
            
        }
        

    }
}