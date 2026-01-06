using System.Threading.Channels;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Models;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

public class ArkLightningInvoiceListener : ILightningInvoiceListener
{
    private readonly string _walletId;
    private readonly ILogger<ArkLightningInvoiceListener> _logger;
    private readonly Network _network;
    private readonly CancellationToken _cancellationToken;
    private readonly ISwapStorage _swapStorage;
    private readonly IDbContextFactory<ArkPluginDbContext> _dbContextFactory;

    private readonly Channel<LightningInvoice> _paidInvoicesChannel = Channel.CreateUnbounded<LightningInvoice>();

    public ArkLightningInvoiceListener(
        string walletId,
        ILogger<ArkLightningInvoiceListener> logger,
        ISwapStorage swapStorage,
        IDbContextFactory<ArkPluginDbContext> dbContextFactory,
        Network network,
        CancellationToken cancellationToken)
    {
        _walletId = walletId;
        _logger = logger;
        _network = network;
        _cancellationToken = cancellationToken;
        _swapStorage = swapStorage;
        _dbContextFactory = dbContextFactory;

        // Subscribe to NNark's swap storage events directly
        _swapStorage.SwapsChanged += OnSwapChanged;
    }

    private async void OnSwapChanged(object? sender, ArkSwap swap)
    {
        try
        {
            // Only process swaps for this wallet that are settled (reverse swaps = receiving)
            if (swap.WalletId != _walletId)
                return;

            if (swap.Status != ArkSwapStatus.Settled)
                return;

            if (swap.SwapType != ArkSwapType.ReverseSubmarine)
                return;

            // Fetch the full entity from DB to get contract data for mapping
            await using var db = await _dbContextFactory.CreateDbContextAsync(_cancellationToken);
            var entity = await db.Swaps
                .Include(s => s.Contract)
                .FirstOrDefaultAsync(s => s.SwapId == swap.SwapId, _cancellationToken);

            if (entity == null)
                return;

            var invoice = ArkLightningClient.Map(entity, _network);
            if (invoice.Status != LightningInvoiceStatus.Paid)
                return;

            await _paidInvoicesChannel.Writer.WriteAsync(invoice, _cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing swap change for {SwapId}", swap.SwapId);
        }
    }

    public async Task<LightningInvoice?> WaitInvoice(CancellationToken cancellation)
    {
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellation);
        
        try
        {
            // Wait for a paid invoice from the channel
            while (await _paidInvoicesChannel.Reader.WaitToReadAsync(combinedCts.Token))
            {
                if (await _paidInvoicesChannel.Reader.ReadAsync(combinedCts.Token) is { } invoice)
                {
                    return invoice;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // This is expected when cancellation is requested
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error waiting for invoice in wallet {WalletId}", _walletId);
        }

        return new LightningInvoice();
    }
    public void Dispose()
    {
        _swapStorage.SwapsChanged -= OnSwapChanged;
        _paidInvoicesChannel.Writer.Complete();
    }
}