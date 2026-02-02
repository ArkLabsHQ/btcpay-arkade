using System.Threading.Channels;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
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
    private readonly IContractStorage _contractStorage;
    private readonly Func<ArkSwap, ArkContractEntity?, Network, LightningInvoice> _mapFunc;

    private readonly Channel<LightningInvoice> _paidInvoicesChannel = Channel.CreateUnbounded<LightningInvoice>();

    public ArkLightningInvoiceListener(
        string walletId,
        ILogger<ArkLightningInvoiceListener> logger,
        ISwapStorage swapStorage,
        IContractStorage contractStorage,
        Network network,
        Func<ArkSwap, ArkContractEntity?, Network, LightningInvoice> mapFunc,
        CancellationToken cancellationToken)
    {
        _walletId = walletId;
        _logger = logger;
        _network = network;
        _cancellationToken = cancellationToken;
        _swapStorage = swapStorage;
        _contractStorage = contractStorage;
        _mapFunc = mapFunc;

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

            // Fetch the contract data for mapping
            ArkContractEntity? contract = null;
            if (!string.IsNullOrEmpty(swap.ContractScript))
            {
                var contracts = await _contractStorage.GetContracts(
                    walletIds: [_walletId],
                    scripts: [swap.ContractScript],
                    cancellationToken: _cancellationToken);
                contract = contracts.FirstOrDefault();
            }

            var invoice = _mapFunc(swap, contract, _network);
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
