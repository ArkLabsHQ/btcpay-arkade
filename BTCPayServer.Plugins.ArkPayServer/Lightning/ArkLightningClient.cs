using System.ComponentModel.DataAnnotations;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.VTXOs;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using NArk.ArkadeIntents.Services;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using NodeInfo = BTCPayServer.Lightning.NodeInfo;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>
/// An Arkade wallet presented to BTCPay as a Lightning node, with the corridors run by an Arkade
/// swap solver.
/// </summary>
/// <remarks>
/// <para>
/// There is no node here and no channels — a Lightning payment in either direction is an Arkade
/// covenant plus a solver willing to take the other side of it. Everything BTCPay asks that depends
/// on actually being a node is unsupported, and the rest is a swap intent wearing an invoice's
/// clothes.
/// </para>
/// <para>
/// The solver is a hard dependency of both corridors and it is optional configuration, so most of
/// what follows starts by asserting it exists. Reading is exempt: a swap already recorded is
/// readable whether or not the solver that made it is still reachable, which matters most when it
/// is not.
/// </para>
/// </remarks>
public class ArkLightningClient(
    IClientTransport clientTransport,
    Network network,
    string walletId,
    ISpendingService spendingService,
    IBitcoinBlockchain chainTimeProvider,
    ILogger<ArkLightningInvoiceListener> logger,
    ArkLightningSpendCapability spendCapability,
    ArkLightningSpendKeyService spendKeyService,
    ArkadeIntentsService? intents = null,
    ArkadeSolverService? solver = null,
    IArkadeIntentStorage? intentStorage = null) : IExtendedLightningClient
{
    /// <summary>
    /// Wallet-level metadata key holding the Lightning spend capability.
    /// See <see cref="ArkLightningSpendCapability"/>.
    /// </summary>
    public const string SpendKeyMetadataKey = "arkade.lightning.spendKey";

    /// <summary>
    /// Throws unless the caller presented the spend capability for this wallet.
    /// </summary>
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

    /// <summary>
    /// The reason this client cannot do anything, or <c>null</c> when it can.
    /// </summary>
    /// <remarks>
    /// The corridors are only registered when an emulator endpoint is configured, because its key is
    /// a parameter of every lockup script — so without one the intent services are genuinely absent
    /// from the container rather than merely idle.
    /// </remarks>
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
        // Filtered here because IArkadeIntentStorage takes no payment-hash filter — the column is
        // indexed, but the abstraction offers no way to reach the index. A merchant's swap set is
        // small and this is off the checkout path, so the scan is worth less than widening the SDK's
        // interface for one caller.
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

    /// <summary>Ask the solver to mint an invoice whose settlement pays this wallet on Arkade.</summary>
    /// <param name="createInvoiceRequest">What BTCPay wants received.</param>
    /// <param name="cancellation">Cancels the negotiation.</param>
    /// <returns>The invoice to hand the payer.</returns>
    /// <remarks>
    /// <para>
    /// The requested amount is what this wallet <em>receives</em>, not what the payer is billed: the
    /// solver's spread is added on top, so the BOLT11 it mints is for more. That direction is the
    /// deliberate one — it guarantees the merchant is credited the amount the order was for, where
    /// billing the order amount and absorbing the spread would settle every order slightly short.
    /// </para>
    /// <para>
    /// The description and expiry BTCPay passes are dropped, because the solver mints the invoice and
    /// nothing in the RFQ request carries either. Its own expiry is what bounds the swap.
    /// </para>
    /// </remarks>
    public async Task<LightningInvoice> CreateInvoice(
        CreateInvoiceParams createInvoiceRequest, CancellationToken cancellation = default)
    {
        await EnsureSpendAuthorized(cancellation);

        var (intents, solver, _) = Corridors;

        var terms = await clientTransport.GetServerInfoAsync(cancellation);
        if (terms.Dust > createInvoiceRequest.Amount)
        {
            throw new InvalidOperationException("Sub-dust amounts are not supported");
        }

        var amountSats = (long)createInvoiceRequest.Amount.ToUnit(LightMoneyUnit.Satoshi);
        var covclaimdKey = await solver.GetCovclaimdKeyAsync(cancellation);

        var pending = await solver.WithTransportAsync(transport =>
            intents.ReceiveFromLightningAsync(
                walletId, amountSats, transport, covclaimdKey, cancellationToken: cancellation));

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

    /// <summary>Pay a BOLT11 by locking sats into a covenant only the solver can claim.</summary>
    /// <param name="bolt11">The invoice to pay.</param>
    /// <param name="payParams">Ignored — the corridor's route is the solver.</param>
    /// <param name="cancellation">Cancels before funding; after funding the swap is live regardless.</param>
    /// <returns>The payment, which is <see cref="LightningPaymentStatus.Pending"/> on success.</returns>
    /// <remarks>
    /// Returns pending rather than complete, always. Funding the lockup is the whole of what this
    /// call does; the solver then pays the invoice and takes the lockup with the preimage, and only
    /// that spend — observed by the monitor — settles the payment. Reporting complete at the point
    /// of funding would call every payment successful, including the ones that end in a refund.
    /// </remarks>
    public async Task<PayResponse> Pay(
        string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = default)
    {
        try
        {
            if (string.IsNullOrEmpty(bolt11))
            {
                throw new NotSupportedException("BOLT11 is required");
            }

            await EnsureSpendAuthorized(cancellation);

            var (intents, solver, _) = Corridors;
            var pr = BOLT11PaymentRequest.Parse(bolt11, network);

            var funded = await solver.WithTransportAsync(transport =>
                intents.SendToLightningAsync(walletId, bolt11, transport, cancellationToken: cancellation));

            var intent = await GetIntentAsync(funded.RfqId, cancellation)
                ?? throw new InvalidOperationException(
                    $"The Arkade send swap '{funded.RfqId}' funded {funded.FundingTxid} but was not recorded.");

            var payment = ArkadeIntentLightningMapper.ToPayment(intent, network);
            return new PayResponse
            {
                Result = PayResult.Ok,
                Details = new PayDetails
                {
                    PaymentHash = pr.PaymentHash,
                    Preimage = string.IsNullOrEmpty(payment.Preimage) ? null : new uint256(payment.Preimage),
                    Status = payment.Status,
                    FeeAmount = payment.Fee,
                    TotalAmount = payment.AmountSent
                }
            };
        }
        catch (Exception e)
        {
            return new PayResponse(PayResult.Error, e.Message);
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
