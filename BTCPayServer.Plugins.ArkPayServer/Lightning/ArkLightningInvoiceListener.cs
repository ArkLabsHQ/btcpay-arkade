using System.Threading.Channels;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using NArk.ArkadeIntents;
using NArk.ArkadeIntents.Models;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

// Signals on Fulfilled (our claim landing), not on the payer's payment: that is a hold only our claim releases.
public class ArkLightningInvoiceListener : ILightningInvoiceListener
{
    private readonly string _walletId;
    private readonly ILogger<ArkLightningInvoiceListener> _logger;
    private readonly Network _network;
    private readonly CancellationToken _cancellationToken;
    private readonly IArkadeIntentStorage _intentStorage;

    private readonly Channel<LightningInvoice> _paidInvoicesChannel = Channel.CreateUnbounded<LightningInvoice>();

    public ArkLightningInvoiceListener(
        string walletId,
        ILogger<ArkLightningInvoiceListener> logger,
        IArkadeIntentStorage intentStorage,
        Network network,
        CancellationToken cancellationToken)
    {
        _walletId = walletId;
        _logger = logger;
        _network = network;
        _cancellationToken = cancellationToken;
        _intentStorage = intentStorage;

        _intentStorage.SwapsChanged += OnSwapChanged;
    }

    private void OnSwapChanged(object? sender, ArkadeSwapIntent intent)
    {
        try
        {
            if (intent.WalletId != _walletId)
                return;

            if (intent.Type != ArkadeSwapIntentType.LightningToBtc)
                return;

            if (intent.Status != ArkadeSwapIntentStatus.Fulfilled)
                return;

            // Via the mapper so the paid rule lives in one place; a swap without an invoice isn't ours.
            if (ArkadeIntentLightningMapper.ToInvoice(intent, _network) is not
                { Status: LightningInvoiceStatus.Paid } invoice)
                return;

            _paidInvoicesChannel.Writer.TryWrite(invoice);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Arkade swap change for {SwapId}", intent.Id);
        }
    }

    /// <summary>
    /// Blocks until one of this wallet's Lightning receives is paid, and throws
    /// <see cref="OperationCanceledException"/> once there will be no more.
    /// </summary>
    /// <remarks>
    /// Returning instead of throwing is not an option BTCPay leaves open: its listen loop reads
    /// <c>notification.Id</c> and looks it up in a dictionary that rejects a null key, so a null or an
    /// empty invoice takes down the notification path and leaves every receive to the one-minute poll.
    /// Cancellation is the loop's own quiet exit, and it reconnects after anything else.
    /// </remarks>
    public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
    {
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellation);

        while (await _paidInvoicesChannel.Reader.WaitToReadAsync(combinedCts.Token))
        {
            if (await _paidInvoicesChannel.Reader.ReadAsync(combinedCts.Token) is { } invoice)
                return invoice;
        }

        throw new OperationCanceledException(
            $"No further Lightning invoices will arrive for wallet {_walletId}.", combinedCts.Token);
    }

    public void Dispose()
    {
        _intentStorage.SwapsChanged -= OnSwapChanged;
        _paidInvoicesChannel.Writer.Complete();
    }
}
