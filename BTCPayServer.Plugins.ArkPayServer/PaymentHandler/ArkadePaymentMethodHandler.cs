using BTCPayServer.Data;
using NArk.ArkadeIntents.Services;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Onchain;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using Microsoft.Extensions.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Services;
using NArk.Core;
using NArk.Abstractions.Contracts;
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
    IWalletStorage walletStorage,
    ILogger<ArkadePaymentMethodHandler> logger,
    ArkadeIntentsService? intents = null,
    ArkadeSolverService? solver = null
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
        // The swap is offered only while this store covers the solver's fee, so the HTLC asks for the
        // order amount like every other rail. Billing the payer instead would need a second amount that
        // one BIP21 cannot carry, and boarding already takes the order amount — slower, but no fee.
        var wantsSwap = arkadePaymentMethodConfig.OnchainSwapEnabled
                        && ArkadeSwapFeePayerSetting.Read(wallet) == ArkadeSwapFeePayer.Recipient;
        // Also derived for the swap: its L1 refund must land on an address registered with this invoice.
        var wantsBoarding = arkadePaymentMethodConfig.BoardingEnabled || wantsSwap;
        if (wantsBoarding &&
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

                var network = btcPayServerEnvironment.NetworkType == ChainName.Mainnet
                    ? Network.Main
                    : btcPayServerEnvironment.NetworkType == ChainName.Testnet
                        ? Network.TestNet
                        : Network.RegTest;
                var boardingAddress = boardingContract.GetOnchainAddress(network);

                context.TrackedDestinations.Add(boardingAddress.ToString());
                context.TrackedDestinations.Add(boardingContract.GetScriptPubKey().ToHex());

                var swap = wantsSwap
                    ? await NegotiateOnchainSwapAsync(
                        arkadePaymentMethodConfig.WalletId, amountSats, boardingAddress, contract)
                    : null;

                // Only learnable after asking: the quote names its own deadline. One that runs out inside
                // the checkout window would leave a payer funding an address nobody is waiting on.
                if (swap is not null
                    && !OnchainSwapInvoicePolicy.CoversCheckout(
                        swap.Quote.ValidUntil, context.InvoiceEntity.ExpirationTime))
                {
                    logger.LogInformation(
                        "Invoice {InvoiceId}: the swap quote expires before the checkout does; offering boarding instead",
                        context.InvoiceEntity.Id);
                    swap = null;
                }

                // An invoice-tagged contract is deactivated once the invoice leaves New, but the swap's
                // refund lands hours later at the L1 locktime; the swap-refund tag keeps it watched.
                // Skipped entirely when neither boarding nor a swap will use it.
                var boardingSource = swap is not null
                    ? $"swap-refund:{swap.RfqId}"
                    : arkadePaymentMethodConfig.BoardingEnabled
                        ? $"invoice:{context.InvoiceEntity.Id}"
                        : null;

                if (boardingSource is not null)
                {
                    await contractService.ImportContract(
                        arkadePaymentMethodConfig.WalletId,
                        boardingContract,
                        metadata: new Dictionary<string, string> { ["Source"] = boardingSource },
                        cancellationToken: CancellationToken.None);

                    // Trigger sync so NBXplorer starts tracking this boarding address immediately
                    _ = Task.Run(() => boardingUtxoSyncService.SyncAsync(CancellationToken.None));
                }

                if (swap is not null)
                {
                    // Replaces boarding: two onchain addresses with different amounts invite a split payment.
                    details = details with
                    {
                        SwapHtlcAddress = swap.HtlcAddress,
                        SwapFundAmountSats = swap.FundAmountSats,
                        SwapId = swap.RfqId,
                    };
                    // For lookup only; it credits nothing. Crediting happens when the claim lands on the prompt's address.
                    context.TrackedDestinations.Add(swap.HtlcAddress);
                }
                else if (arkadePaymentMethodConfig.BoardingEnabled)
                {
                    details = details with
                    {
                        BoardingAddress = boardingAddress.ToString(),
                        BoardingContractString = boardingContract.ToString(),
                    };
                }
        }

        context.Prompt.Details = JObject.FromObject(details, Serializer);
    }

    // Returns null on any failure: a missing or unwilling solver means offering boarding, not failing checkout.
    // Exact-in: the HTLC asks for the order amount, and the invoice is credited with what the payer sent
    // rather than the smaller amount that lands, so every rail on the payment link shares one amount.
    // The payout must be the prompt's contract: ArkContractInvoiceListener only credits addresses in
    // TrackedDestinations, so a fresh one would be claimed but leave the invoice unpaid. It also saves an HD index.
    private async Task<PendingOnchainReceive?> NegotiateOnchainSwapAsync(
        string walletId, long amountSats, BitcoinAddress refundDestination, ArkContract payoutContract)
    {
        if (intents is null || solver is null) return null;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (!await solver.HasSolverForAsync(
                    ArkadeSolverSelector.OnchainCorridor, timeout.Token))
            {
                return null;
            }

            var covclaimd = await solver.ResolveClaimRecipientAsync(timeout.Token);
            return await solver.WithTransportAsync(
                amountSats, ArkadeSolverSelector.OnchainCorridor,
                (transport, card) => intents.ReceiveFromOnchainAsync(
                    walletId, amountSats, transport, covclaimd, refundDestination,
                    amountSide: RfqAmountSide.From, solverCard: card,
                    payoutContract: payoutContract, cancellationToken: timeout.Token),
                timeout.Token, fallBack: true);
        }
        catch (Exception e)
        {
            logger.LogInformation(
                e, "No onchain swap for this invoice; offering the boarding path instead");
            return null;
        }
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
