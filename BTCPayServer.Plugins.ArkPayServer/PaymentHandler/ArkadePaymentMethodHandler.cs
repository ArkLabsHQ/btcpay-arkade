using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services;
using NArk.Core;
using NArk.Abstractions.Wallets;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public class ArkadePaymentMethodHandler(
    BTCPayServerEnvironment btcPayServerEnvironment,
    IContractService contractService,
    IClientTransport clientTransport,
    BoardingUtxoSyncService boardingUtxoSyncService,
    IWalletStorage walletStorage
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

        var contract = await contractService.DeriveContract(
            arkadePaymentMethodConfig.WalletId,
            NextContractPurpose.Receive,
            metadata: new Dictionary<string, string> { ["Source"] = $"invoice:{context.InvoiceEntity.Id}" },
            cancellationToken: CancellationToken.None);
        var details = new ArkadePromptDetails(arkadePaymentMethodConfig.WalletId, contract);
        var address = contract.GetArkAddress();

        context.Prompt.Destination = address.ToString(btcPayServerEnvironment.NetworkType == ChainName.Mainnet);
        context.Prompt.PaymentMethodFee = 0m;

        context.TrackedDestinations.Add(context.Prompt.Destination);
        context.TrackedDestinations.Add(address.ScriptPubKey.PaymentScript.ToHex());

        // Derive boarding address when: boarding enabled, no onchain BTC configured, HD wallet, amount above threshold.
        // SingleKey (nsec) wallets are excluded because they derive the same boarding address for every invoice,
        // causing duplicate AddressInvoice conflicts.
        // Reuse the same signing descriptor from the Ark payment contract to avoid consuming an extra HD index.
        var hasOnchain = context.InvoiceEntity.GetPaymentPrompt(PaymentTypes.CHAIN.GetPaymentMethodId("BTC")) is not null;
        var wallet = await walletStorage.GetWalletById(arkadePaymentMethodConfig.WalletId);
        var amountSats = Money.Coins(context.Prompt.Calculate().Due).Satoshi;
        if (arkadePaymentMethodConfig.BoardingEnabled &&
            !hasOnchain && wallet?.WalletType == WalletType.HD &&
            amountSats >= arkadePaymentMethodConfig.MinBoardingAmountSats)
        {
                // Construct boarding contract from same user descriptor — no extra DeriveContract call
                var userDescriptor = contract switch
                {
                    ArkPaymentContract pc => pc.User,
                    ArkDelegateContract dc => dc.User,
                    _ => throw new PaymentMethodUnavailableException("Unsupported contract type for boarding")
                };
                var boardingContract = new ArkBoardingContract(
                    serverInfo.SignerKey, serverInfo.BoardingExit, userDescriptor);
                await contractService.ImportContract(
                    arkadePaymentMethodConfig.WalletId,
                    boardingContract,
                    metadata: new Dictionary<string, string> { ["Source"] = $"invoice:{context.InvoiceEntity.Id}" },
                    cancellationToken: CancellationToken.None);

                var network = btcPayServerEnvironment.NetworkType == ChainName.Mainnet
                    ? Network.Main
                    : btcPayServerEnvironment.NetworkType == ChainName.Testnet
                        ? Network.TestNet
                        : Network.RegTest;
                var boardingAddress = boardingContract.GetOnchainAddress(network);
                details = details with { BoardingAddress = boardingAddress.ToString() };
                context.TrackedDestinations.Add(boardingAddress.ToString());
                context.TrackedDestinations.Add(boardingContract.GetScriptPubKey().ToHex());

                // Trigger sync so NBXplorer starts tracking this boarding address immediately
                _ = Task.Run(() => boardingUtxoSyncService.SyncAsync(CancellationToken.None));
        }

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
