using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Models.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk;
using NArk.Contracts;
using NArk.Extensions;
using NBXplorer;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkadeContractSweeper : IHostedService
{
    private readonly ArkadeSpender _arkadeSpender;
    private readonly ArkWalletService _arkWalletService;
    private readonly EventAggregator _eventAggregator;
    private readonly ILogger<ArkadeContractSweeper> _logger;
    private readonly ArkVtxoSynchronizationService _arkSubscriptionService;
    private CompositeDisposable _leases = new();
    private CancellationTokenSource _cts = new();
    private TaskCompletionSource? _tcsWaitForNextPoll;

    public ArkadeContractSweeper(
        ArkadeSpender arkadeSpender,
        ArkWalletService arkWalletService,
        EventAggregator eventAggregator,
        ILogger<ArkadeContractSweeper> logger,
        ArkVtxoSynchronizationService arkSubscriptionService)
    {
        _arkadeSpender = arkadeSpender;
        _arkWalletService = arkWalletService;
        _eventAggregator = eventAggregator;
        _logger = logger;
        _arkSubscriptionService = arkSubscriptionService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _leases.Add(_eventAggregator.SubscribeAsync<VTXOsUpdated>(OnVTXOsUpdated));
        _ = PollForVTXOToSweep();
        return Task.CompletedTask;
    }

    private async Task PollForVTXOToSweep()
    {
        await _arkSubscriptionService.Started;
        while (!_cts.IsCancellationRequested)
        {
            _logger.LogInformation("Polling for vtxos to sweep.");
            try
            {
                var spendableCoinsByWallet = await _arkadeSpender.GetSpendableCoins(null, false, _cts.Token);
                var wallets = await _arkWalletService.GetWallets(spendableCoinsByWallet.Keys.ToArray(), _cts.Token);
                
                foreach (var group in spendableCoinsByWallet)
                {
                    try
                    {
                        var wallet = wallets.First(x => x.Id == group.Key);
                        var coinsToSweep = GetCoinsToSweep(wallet, group.Value);

                        if (coinsToSweep.Count == 0)
                        {
                            _logger.LogTrace("Skipping sweep for wallet {WalletId}: no coins need sweeping", wallet.Id);
                            continue;
                        }

                        _logger.LogInformation("Sweeping {Count} coins for wallet {WalletId}", coinsToSweep.Count, wallet.Id);
                        await _arkadeSpender.Spend(wallet, coinsToSweep, [], _cts.Token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error while sweeping vtxos for wallet {WalletId}", group.Key);
                    }
                }
                
                _tcsWaitForNextPoll = new TaskCompletionSource();
                using var cts2 = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cts2.Token);
                linkedCts.Token.Register(() => _tcsWaitForNextPoll.TrySetCanceled());
                await _tcsWaitForNextPoll.Task;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while sweeping vtxos");
                await Task.Delay(TimeSpan.FromMinutes(1), _cts.Token);
            }
        }
    }

    /// <summary>
    /// Filters coins that need to be swept based on wallet type and contract type.
    /// - Single-key wallets: sweep all coins not at default destination or explicit destination
    /// - HD wallets: only sweep non-payment contracts (VHTLCs, etc.)
    /// </summary>
    private List<SpendableArkCoinWithSigner> GetCoinsToSweep(ArkWallet wallet, List<SpendableArkCoinWithSigner> coins)
    {
        var isHDWallet = wallet.WalletType == WalletType.Mnemonic;
        var coinsToSweep = new List<SpendableArkCoinWithSigner>();

        foreach (var coin in coins)
        {
            // Skip coins already at explicit destination
            if (wallet.Destination is not null && coin.TxOut.IsTo(wallet.Destination))
            {
                continue;
            }

            if (isHDWallet)
            {
                // For HD wallets, only sweep non-payment contracts
                // Payment contracts are the final destination - don't sweep
                if (coin.Contract is ArkPaymentContract or HashLockedArkPaymentContract)
                {
                    continue;
                }
            }
            else
            {
                // For single-key wallets, skip coins already at the wallet's default payment contract
                // (coins belonging to the wallet's own key)
                if (coin.Contract is ArkPaymentContract paymentContract)
                {
                    // Check if this payment contract belongs to this wallet
                    if (paymentContract.User.WalletId().Equals(wallet.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }
            }

            coinsToSweep.Add(coin);
        }

        return coinsToSweep;
    }

    private Task OnVTXOsUpdated(VTXOsUpdated arg)
    {
        _tcsWaitForNextPoll?.TrySetResult();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cts.CancelAsync();
        _leases.Dispose();
        _leases = new CompositeDisposable();
    }
}