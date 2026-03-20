using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Core.Contracts;
using NArk.Core.Services;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Listens for real-time on-chain transaction events from NBXplorer and triggers
/// boarding UTXO synchronization only for the specific contracts that received funds.
/// </summary>
public class BoardingTransactionListener : EventHostedServiceBase
{
    private readonly BoardingUtxoSyncService _boardingUtxoSyncService;
    private readonly IContractStorage _contractStorage;
    private readonly ILogger<BoardingTransactionListener> _logger;

    public BoardingTransactionListener(
        EventAggregator eventAggregator,
        BoardingUtxoSyncService boardingUtxoSyncService,
        IContractStorage contractStorage,
        ILogger<BoardingTransactionListener> logger)
        : base(eventAggregator, logger)
    {
        _boardingUtxoSyncService = boardingUtxoSyncService;
        _contractStorage = contractStorage;
        _logger = logger;
    }

    protected override void SubscribeToEvents()
    {
        Subscribe<NewOnChainTransactionEvent>();
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is not NewOnChainTransactionEvent txEvent)
            return;

        if (txEvent.PaymentMethodId.ToString() != "BTC-CHAIN")
            return;

        var tx = txEvent.NewTransactionEvent.TransactionData.Transaction;
        var outputScripts = tx.Outputs.Select(o => o.ScriptPubKey.ToHex()).ToHashSet();

        // Query only boarding contracts whose scripts appear in this transaction's outputs
        var matchedContracts = await _contractStorage.GetContracts(
            scripts: outputScripts.ToArray(),
            contractTypes: [ArkBoardingContract.ContractType],
            cancellationToken: cancellationToken);

        if (matchedContracts.Count == 0)
            return;

        _logger.LogInformation(
            "Transaction {TxId} touches {Count} boarding contract(s), syncing...",
            tx.GetHash().ToString()[..8], matchedContracts.Count);

        try
        {
            await _boardingUtxoSyncService.SyncAsync(matchedContracts, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync boarding UTXOs after transaction event");
        }
    }
}
