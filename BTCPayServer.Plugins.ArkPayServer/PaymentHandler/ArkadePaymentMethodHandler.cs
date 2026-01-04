using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services;
using NArk;
using NArk.Transport;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public class ArkadePaymentMethodHandler(
    BTCPayServerEnvironment btcPayServerEnvironment,
    ArkWalletService arkWalletService,
    IClientTransport clientTransport
) : IPaymentMethodHandler
{
    public PaymentMethodId PaymentMethodId => ArkadePlugin.ArkadePaymentMethodId;

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        ArkServerInfo serverInfo;
        try
        {
            serverInfo = await clientTransport.GetServerInfoAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
        catch
        {
            throw new PaymentMethodUnavailableException("Ark operator unavailable");
        }

        var store = context.Store;

        if (ParsePaymentMethodConfig(store.GetPaymentMethodConfigs()[PaymentMethodId]) is not ArkadePaymentMethodConfig
            arkadePaymentMethodConfig)
        {
            throw new PaymentMethodUnavailableException("Arkade payment method not configured");
        }

        if (!arkadePaymentMethodConfig.AllowSubDustAmounts && Money.Coins(context.Prompt.Calculate().Due) < serverInfo.Dust)
        {
            throw new PaymentMethodUnavailableException("Amount too small");
        }

        var contract = await arkWalletService.DerivePaymentContract(arkadePaymentMethodConfig.WalletId, CancellationToken.None);
        var details = new ArkadePromptDetails(arkadePaymentMethodConfig.WalletId, contract);
        var address = contract.GetArkAddress();

        context.Prompt.Destination = address.ToString(btcPayServerEnvironment.NetworkType == ChainName.Mainnet);
        context.Prompt.PaymentMethodFee = 0m;

        context.TrackedDestinations.Add(context.Prompt.Destination);
        context.TrackedDestinations.Add(address.ScriptPubKey.PaymentScript.ToHex());
        context.Prompt.Details = JObject.FromObject(details, Serializer);
    }

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = "BTC";
        context.Prompt.Divisibility = 8;
        return Task.CompletedTask;
    }

    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;

    public ArkadePromptDetails ParsePaymentPromptDetails(JToken details)
    {
        return details.ToObject<ArkadePromptDetails>(Serializer);
    }

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details)
    {
        return ParsePaymentPromptDetails(details);
    }

    public object ParsePaymentMethodConfig(JToken config)
    {
        return config.ToObject<ArkadePaymentMethodConfig>(Serializer) ??
               throw new FormatException($"Invalid {nameof(ArkadePaymentMethodHandler)}");
    }

    public ArkadePaymentData ParsePaymentDetails(JToken details)
    {
        return details.ToObject<ArkadePaymentData>(Serializer) ??
               throw new FormatException($"Invalid {nameof(ArkadePaymentData)}");
    }
    object IPaymentMethodHandler.ParsePaymentDetails(JToken details)
    {
        return ParsePaymentDetails(details);
    }

    public void StripDetailsForNonOwner(object details)
    {
    }
}
