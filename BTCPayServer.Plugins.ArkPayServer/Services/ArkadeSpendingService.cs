using System.Globalization;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Exceptions;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using NArk;
using NArk.Services.Abstractions;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkadeSpendingService(
    ArkWalletService arkWalletService,
    ArkadeSpender arkadeSpender,
    IOperatorTermsService operatorTermsService,
    PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
    ArkIntentService arkIntentService)
{
    public async Task<string?> Spend(StoreData store, string destination, CancellationToken cancellationToken)
    {
        destination = destination.Trim();
        ArgumentNullException.ThrowIfNull(store);
        
        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            throw new IncompleteArkadeSetupException("arkade wallet setup was not done!");

        if (!config.GeneratedByStore)
            throw new IncompleteArkadeSetupException("Wallet does not belong to the current store.");
        
        var terms = await operatorTermsService.GetOperatorTerms(cancellationToken);
        
        if (destination.Replace("lightning:", "", StringComparison.InvariantCultureIgnoreCase) is { } lnbolt11 &&
            BOLT11PaymentRequest.TryParse(lnbolt11, out var bolt11, terms.Network))
        {
            if (bolt11 is null)
            {
                throw new MalformedPaymentDestination();
            }

            var lnConfig =
                store
                    .GetPaymentMethodConfig<LightningPaymentMethodConfig>(
                        GetLightningPaymentMethod(),
                        paymentMethodHandlerDictionary
                    );

            if (lnConfig is null)
            {
                throw new IncompleteArkadeSetupException("lightning compatibility is not enabled");
            }

            var lnClient = paymentMethodHandlerDictionary.GetLightningHandler("BTC").CreateLightningClient(lnConfig);
            var resp = await lnClient.Pay(bolt11.ToString(), cancellationToken);
            if (resp.Result == PayResult.Ok)
            {
                return null;
            }

            throw new ArkadePaymentFailedException($"Payment failed: {resp?.ErrorDetail}");
        }
        else if (Uri.TryCreate(destination, UriKind.Absolute, out var uri) && uri.Scheme.Equals("bitcoin", StringComparison.InvariantCultureIgnoreCase))
        {
            var host = uri.AbsoluteUri[(uri.Scheme.Length + 1)..].Split('?')[0]; // uri.Host is empty so we must parse it ourselves

            var qs = uri.ParseQueryString();
            if (ArkAddress.TryParse(host, out var address) ||
                (qs["ark"] is { } arkQs && ArkAddress.TryParse(arkQs, out address)))
            {
                if (address is null)
                {
                    throw new MalformedPaymentDestination();
                }

                var amount = decimal.Parse(qs["amount"] ?? "0", CultureInfo.InvariantCulture);

                try
                {
                    var txId = await arkadeSpender.Spend(config.WalletId, [new TxOut(Money.Coins(amount), address)],
                        cancellationToken);

                    await arkWalletService.UpdateBalances(config.WalletId, true, cancellationToken);

                    return txId.ToString();
                }
                catch (Exception e)
                {
                    throw new ArkadePaymentFailedException(e.Message);
                }
            }
            else if (BitcoinAddress.Create(host, terms.Network) is {} onchainAddress)
            {
                var amount = 
                    decimal.Parse(qs["amount"] ?? "0", CultureInfo.InvariantCulture);

                if (amount < terms.Dust.ToDecimal(MoneyUnit.BTC))
                {
                    throw new ArkadePaymentFailedException("Amount is below dust threshold.");
                }

                var (coins, intentOutputs) =
                    await arkadeSpender.PrepareOnchainSpend(config.WalletId, new TxOut(Money.Coins(amount), onchainAddress), cancellationToken);

                var intentId = await arkIntentService.CreateIntentAsync(config.WalletId, coins, intentOutputs, cancellationToken: cancellationToken);

                return intentId;
            }
        }

        throw new MalformedPaymentDestination();
    }
    
    private static PaymentMethodId GetLightningPaymentMethod() => PaymentTypes.LN.GetPaymentMethodId("BTC");

    private T? GetConfig<T>(PaymentMethodId paymentMethodId, StoreData store) where T : class
    {
        return store.GetPaymentMethodConfig<T>(paymentMethodId, paymentMethodHandlerDictionary);
    }

}