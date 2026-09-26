using System.ComponentModel.DataAnnotations;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Lightning;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Rfq;
using NArk.ArkadeIntents.Services;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using NodeInfo = BTCPayServer.Lightning.NodeInfo;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>
/// An Arkade wallet presented to BTCPay as a Lightning node: each payment is a covenant plus a solver
/// taking the other side. Reads work without a solver, so recorded swaps stay visible when it is gone.
/// </summary>
public class ArkLightningClient(
    IClientTransport clientTransport,
    Network network,
    string walletId,
    ISpendingService spendingService,
    IBitcoinBlockchain chainTimeProvider,
    ILogger<ArkLightningInvoiceListener> logger,
    ArkLightningSpendCapability spendCapability,
    ArkLightningSpendKeyService spendKeyService,
    IWalletStorage walletStorage,
    IWalletProvider walletProvider,
    ArkadeIntentsService? intents = null,
    ArkadeSolverService? solver = null,
    IArkadeIntentStorage? intentStorage = null) : IExtendedLightningClient
{
    /// <summary>
    /// Wallet-level metadata key holding the Lightning spend capability.
    /// See <see cref="ArkLightningSpendCapability"/>.
    /// </summary>
    public const string SpendKeyMetadataKey = "arkade.lightning.spendKey";

    /// <summary>Throws unless the caller presented the spend capability. Guards paying only: an
    /// invoice moves money into the wallet, so minting is no way to spend someone else's.</summary>
    private async Task EnsureSpendAuthorized(CancellationToken cancellation)
    {
        if (await spendKeyService.VerifyAsync(walletId, spendCapability.Value, cancellation))
            return;

        logger.LogWarning(
            "Rejected an Arkade Lightning spend for wallet {WalletId}: no valid spend " +
            "capability was presented.", walletId);
        throw new UnauthorizedAccessException(
            "This store is not authorised to spend from the configured Arkade wallet.");
    }

    // No signer means no claim: we do not spend the signerless leaf, and no covclaimd does it for us.
    // TODO: drop once covclaimd claims for us, or an emulator enables SignerlessFallback.
    private async Task EnsureCanClaim(CancellationToken cancellation)
    {
        if (await walletProvider.GetSignerAsync(walletId, cancellation) is not null)
            return;

        throw new NotSupportedException(
            "This Arkade wallet cannot sign, so a Lightning payment to it could not be claimed. " +
            "Import it with its key, or connect the BTCPay App companion plugin to sign remotely.");
    }

    // Without an emulator the corridor services are absent from the container, not merely idle.
    private string? Unavailable =>
        intents is null || solver is null || intentStorage is null
            ? "The Arkade Lightning corridors are not configured. Set 'emulator' in the Arkade " +
              "network configuration to enable them."
            : null;

    private (ArkadeIntentsService Intents, ArkadeSolverService Solver, IArkadeIntentStorage Storage) Corridors =>
        Unavailable is { } reason
            ? throw new InvalidOperationException(reason)
            : (intents!, solver!, intentStorage!);

    /// <summary>Every swap this wallet owns, newest first.</summary>
    private async Task<List<ArkadeSwapIntent>> GetIntentsAsync(
        ArkadeSwapIntentType type, CancellationToken cancellation)
    {
        if (intentStorage is null) return [];

        var all = await intentStorage.GetArkadeSwapIntents(
            walletIds: [walletId], cancellationToken: cancellation);

        return all.Where(i => i.Type == type).OrderByDescending(i => i.CreatedAt).ToList();
    }

    private async Task<ArkadeSwapIntent?> GetIntentAsync(string id, CancellationToken cancellation) =>
        intentStorage is null ? null : await intentStorage.GetArkadeSwapIntent(id, cancellation);

    // ─── Receiving ────────────────────────────────────────────────────

    public async Task<LightningInvoice?> GetInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        var intent = await GetIntentAsync(invoiceId, cancellation);
        return intent is { Type: ArkadeSwapIntentType.LightningToBtc } && intent.WalletId == walletId
            ? ArkadeIntentLightningMapper.ToInvoice(intent, network)
            : null;
    }

    public async Task<LightningInvoice?> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default)
    {
        // IArkadeIntentStorage has no payment-hash filter; a merchant's swap set is small enough to scan.
        var hash = paymentHash.ToString();
        var intents = await GetIntentsAsync(ArkadeSwapIntentType.LightningToBtc, cancellation);
        var match = intents.FirstOrDefault(i =>
            string.Equals(i.PaymentHash, hash, StringComparison.OrdinalIgnoreCase));

        return match is null ? null : ArkadeIntentLightningMapper.ToInvoice(match, network);
    }

    public Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = default) =>
        ListInvoices(new ListInvoicesParams(), cancellation);

    public async Task<LightningInvoice[]> ListInvoices(
        ListInvoicesParams request, CancellationToken cancellation = default)
    {
        var intents = await GetIntentsAsync(ArkadeSwapIntentType.LightningToBtc, cancellation);

        return
        [
            .. intents
                .Skip((int)request.OffsetIndex.GetValueOrDefault(0))
                .Select(i => ArkadeIntentLightningMapper.ToInvoice(i, network))
                .OfType<LightningInvoice>()
                .Where(i => request.PendingOnly != true || i.Status == LightningInvoiceStatus.Unpaid)
        ];
    }

    public Task<LightningInvoice> CreateInvoice(
        LightMoney amount, string description, TimeSpan expiry, CancellationToken cancellation = default) =>
        CreateInvoice(new CreateInvoiceParams(amount, description, expiry), cancellation);

    // Which leg the order amount pins is the store's choice: exact-in bills the payer that amount and
    // nets the fee out of the payout, exact-out mints an invoice above it, which LUD-06 wallets refuse.
    // BTCPay's description and expiry are dropped; the solver mints the invoice and the RFQ carries neither.
    public async Task<LightningInvoice> CreateInvoice(
        CreateInvoiceParams createInvoiceRequest, CancellationToken cancellation = default)
    {
        await EnsureCanClaim(cancellation);

        var (intents, solver, _) = Corridors;

        var terms = await clientTransport.GetServerInfoAsync(cancellation);
        if (terms.Dust > createInvoiceRequest.Amount)
        {
            throw new InvalidOperationException("Sub-dust amounts are not supported");
        }

        var amountSats = (long)createInvoiceRequest.Amount.ToUnit(LightMoneyUnit.Satoshi);
        var claimRecipient = await solver.ResolveClaimRecipientAsync(cancellation);

        var amountSide = ArkadeSwapFeePayerSetting.AmountSide(
            await ArkadeSwapFeePayerSetting.ReadAsync(walletStorage, walletId, cancellation));

        // Safe to try another solver: a receive negotiation commits nothing of ours — the solver funds
        // the lockup, and the swap is recorded only once a quote comes back.
        var pending = await solver.WithTransportAsync(amountSats, (transport, card) =>
            intents.ReceiveFromLightningAsync(
                walletId, amountSats, transport, claimRecipient, card,
                amountSide: amountSide, cancellationToken: cancellation), cancellation, fallBack: true);

        var intent = await GetIntentAsync(pending.RfqId, cancellation)
            ?? throw new InvalidOperationException(
                $"The Arkade receive swap '{pending.RfqId}' was negotiated but not recorded.");

        return ArkadeIntentLightningMapper.ToInvoice(intent, network)
            ?? throw new InvalidOperationException(
                $"The Arkade receive swap '{pending.RfqId}' was recorded without the solver's invoice.");
    }

    public Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default)
    {
        var (_, _, storage) = Corridors;
        return Task.FromResult<ILightningInvoiceListener>(
            new ArkLightningInvoiceListener(walletId, logger, storage, network, cancellation));
    }

    // ─── Paying ───────────────────────────────────────────────────────

    public async Task<LightningPayment> GetPayment(string paymentHash, CancellationToken cancellation = default)
    {
        var intents = await GetIntentsAsync(ArkadeSwapIntentType.BtcToLightning, cancellation);
        var match = intents.FirstOrDefault(i =>
            string.Equals(i.PaymentHash, paymentHash, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("Swap with the given payment hash was not found");

        return ArkadeIntentLightningMapper.ToPayment(match, network);
    }

    public Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default) =>
        ListPayments(new ListPaymentsParams(), cancellation);

    public async Task<LightningPayment[]> ListPayments(
        ListPaymentsParams request, CancellationToken cancellation = default)
    {
        var intents = await GetIntentsAsync(ArkadeSwapIntentType.BtcToLightning, cancellation);

        return
        [
            .. intents
                .Skip((int)request.OffsetIndex.GetValueOrDefault(0))
                .Select(i => ArkadeIntentLightningMapper.ToPayment(i, network))
        ];
    }

    public Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = default) =>
        throw new NotSupportedException("BOLT11 is required");

    public Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default) =>
        Pay(bolt11, new PayInvoiceParams(), cancellation);

    // Always pending: funding the lockup is all this does, and a refund must not be reported as success.
    public async Task<PayResponse> Pay(
        string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = default)
    {
        BOLT11PaymentRequest? pr = null;
        FundedLightningSend funded;
        try
        {
            if (string.IsNullOrEmpty(bolt11))
            {
                throw new NotSupportedException("BOLT11 is required");
            }

            await EnsureSpendAuthorized(cancellation);

            var (intents, solver, _) = Corridors;
            pr = BOLT11PaymentRequest.Parse(bolt11, network);

            // The Lightning leg; understates the trade by the fee, which is fine for picking a solver.
            var amountSats = (long)pr.MinimumAmount.ToUnit(LightMoneyUnit.Satoshi);

            funded = await solver.WithTransportAsync(amountSats, (transport, card) =>
                intents.SendToLightningAsync(walletId, bolt11, transport, card, cancellation),
                cancellation);
        }
        catch (Exception e)
        {
            return new PayResponse(PayResult.Error, e.Message);
        }

        // Funded, or possibly funded, from here on, so nothing below may answer Error: BTCPay would retry and pay twice.
        if (!funded.FundingConfirmed)
            return ArkadeIntentLightningMapper.InFlight(pr, $"The funding outcome of Arkade send swap '{funded.RfqId}' is unknown.");

        try
        {
            var intent = await GetIntentAsync(funded.RfqId, cancellation);
            return intent is null
                ? ArkadeIntentLightningMapper.InFlight(pr, $"The Arkade send swap '{funded.RfqId}' was not found after funding.")
                : ArkadeIntentLightningMapper.ToPayResponse(intent, pr, network);
        }
        catch (Exception e)
        {
            return ArkadeIntentLightningMapper.InFlight(pr, e.Message);
        }
    }

    // ─── Wallet ───────────────────────────────────────────────────────

    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = default)
    {
        var availableCoins = await spendingService.GetAvailableCoins(walletId, cancellation);
        var chainTime = await chainTimeProvider.GetChainTime(cancellation);

        // Filter to only coins that can be spent offchain (not swept, not expired)
        var spendableCoins = availableCoins.Where(c => c.CanSpendOffchain(chainTime));
        var sum = spendableCoins.Sum(c => c.TxOut.Value.Satoshi);

        return new LightningNodeBalance
        {
            OffchainBalance = new OffchainBalance
            {
                Local = LightMoney.Satoshis(sum)
            }
        };
    }

    public Task<ValidationResult?> Validate() =>
        Task.FromResult(Unavailable is { } reason ? new ValidationResult(reason) : ValidationResult.Success);

    // ─── Not a node ───────────────────────────────────────────────────

    public Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default) =>
        throw new NotSupportedException();

    public Task<OpenChannelResponse> OpenChannel(
        OpenChannelRequest openChannelRequest, CancellationToken cancellation = default) =>
        throw new NotSupportedException();

    public Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = default) =>
        throw new NotSupportedException();

    public Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = default) =>
        throw new NotSupportedException();

    public Task CancelInvoice(string invoiceId, CancellationToken cancellation = default) =>
        throw new NotSupportedException();

    public Task<LightningChannel[]> ListChannels(CancellationToken cancellation = default) =>
        throw new NotSupportedException();

    public string DisplayName => "Arkade Lightning";
    public Uri? ServerUri => null;

    public override string ToString() => $"type=arkade;wallet-id={walletId}";
}
