using Ark.V1;
using AsyncKeyedLock;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NArk;
using NArk.Contracts;
using NArk.Models;
using NArk.Scripts;
using NArk.Services;
using NArk.Services.Abstractions;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkadeSpender(
    AsyncKeyedLocker asyncKeyedLocker,
    ArkadeWalletSignerProvider arkadeWalletSignerProvider,
    ArkTransactionBuilder arkTransactionBuilder,
    ArkService.ArkServiceClient arkServiceClient,
    ArkWalletService arkWalletService,
    ILogger<ArkadeSpender> logger,
    IOperatorTermsService operatorTermsService,
    ArkVtxoSynchronizationService arkVtxoSynchronizationService,
    BitcoinTimeChainProvider bitcoinTimeChainProvider)
{
    public async Task<(SpendableArkCoinWithSigner[], IntentTxOut[])> PrepareOnchainSpend(string walletId, TxOut onchainDestination, CancellationToken cancellationToken = default)
    {
        using var l = await asyncKeyedLocker.LockAsync($"ark-{walletId}-txs-spending", cancellationToken);

        var coinSet = await GetSpendableCoins([walletId],false, cancellationToken);

        if (!coinSet.TryGetValue(walletId, out var coins) || coins.Count == 0)
        {
            throw new InvalidOperationException($"No coins to spend for wallet {walletId}");
        }

        logger.LogInformation("Found {VtxoCount} VTXOs to spend for wallet {WalletId}", coins.Count, walletId);

        var wallet = 
            await arkWalletService.GetWallet(walletId, cancellationToken) 
                ?? throw new InvalidOperationException($"Wallet {walletId} not found");

        var operatorTerms = await operatorTermsService.GetOperatorTerms(cancellationToken);

        var changeAddress = await GetDestination(wallet, operatorTerms);

        var totalOutput = onchainDestination.Value + (operatorTerms.FeeTerms.OnchainOutput * 2);
        var availableCoins = coins.OrderByDescending(x => x.TxOut.Value).ToList();
        
        // Check if output is explicitly subdust
        if (onchainDestination.Value < operatorTerms.Dust)
        {
            throw new NotImplementedException("Sending explicit subdust outputs is not supported yet.");
        }
        
        // Select coins based on useAllCoins parameter
        List<SpendableArkCoinWithSigner>? selectedCoins =
            SelectCoins(availableCoins, totalOutput, operatorTerms, 0);

        if (selectedCoins == null || selectedCoins.Count == 0)
            throw new InvalidOperationException(
                $"Insufficient funds. Available: {availableCoins.Sum(x => x.TxOut.Value)}, Required: {totalOutput}");
        
        var totalInput = selectedCoins.Sum(x => x.TxOut.Value);
        var change = totalInput - onchainDestination.Value - (selectedCoins.Count * operatorTerms.FeeTerms.OffchainInput);
        
        IntentTxOut[] intentOutputs;

        // Add change output if it's at or above dust threshold
        if (change >= operatorTerms.Dust)
        {
            var onchainIntentDestination = 
                new IntentTxOut(onchainDestination, IntentTxOut.IntentOutputType.OnChain);

            intentOutputs = [onchainIntentDestination, new IntentTxOut(new TxOut(Money.Satoshis(change) - (operatorTerms.FeeTerms.OffchainOutput + operatorTerms.FeeTerms.OnchainOutput), changeAddress), IntentTxOut.IntentOutputType.VTXO)];
        }
        else
        {
            intentOutputs = [new IntentTxOut(new TxOut(onchainDestination.Value + change, onchainDestination.ScriptPubKey),IntentTxOut.IntentOutputType.OnChain)];
        }

        return ([.. selectedCoins], intentOutputs);
    }

    public async Task<uint256> Spend(string walletId, TxOut[] outputs, CancellationToken cancellationToken = default)
    {
        var coinSet = await GetSpendableCoins([walletId],false, cancellationToken);

        if (!coinSet.TryGetValue(walletId, out var coins) || coins.Count == 0)
        {
            throw new InvalidOperationException($"No coins to spend for wallet {walletId}");
        }

        logger.LogInformation($"Found {coins.Count} VTXOs to spend for wallet {walletId}");

        var wallet = await arkWalletService.GetWallet(walletId, cancellationToken);

        if (wallet is null)
        {
            throw new InvalidOperationException($"Wallet {walletId} not found");
        }

        return await Spend(wallet, coins, outputs, cancellationToken);
    }

    public async Task<uint256> Spend(ArkWallet wallet, IEnumerable<SpendableArkCoinWithSigner> coins, TxOut[] outputs,
        CancellationToken cancellationToken = default)
    {
        return await Spend(wallet, coins, outputs, true, cancellationToken);
    }

    public async Task<uint256> Spend(ArkWallet wallet, IEnumerable<SpendableArkCoinWithSigner> coins, TxOut[] outputs,
        bool useAllCoins, CancellationToken cancellationToken = default)
    {
        using var l = await asyncKeyedLocker.LockAsync($"ark-{wallet.Id}-txs-spending", cancellationToken);
        var operatorTerms = await operatorTermsService.GetOperatorTerms(cancellationToken);
        var destination = await GetDestination(wallet, operatorTerms);
        return await SpendWalletCoins(coins, operatorTerms, outputs, destination, useAllCoins, cancellationToken);
    }

    private async Task<uint256> SpendWalletCoins(IEnumerable<SpendableArkCoinWithSigner> coins,
        ArkOperatorTerms operatorTerms, TxOut[] outputs, ArkAddress changeAddress, bool useAllCoins, CancellationToken cancellationToken)
    {
        var totalOutput = outputs.Sum(x => x.Value) + (outputs.Length * operatorTerms.FeeTerms.OffchainOutput);
        var availableCoins = coins.OrderByDescending(x => x.TxOut.Value).ToList();
        
        // Check if any output is explicitly subdust (user wants to send subdust amount)
        var hasExplicitSubdustOutput = outputs.Count(o => o.Value < operatorTerms.Dust);
        
        // Select coins based on useAllCoins parameter
        List<SpendableArkCoinWithSigner>? selectedCoins;
        if (useAllCoins)
        {
            // Use all available coins
            selectedCoins = availableCoins;
            logger.LogInformation("Using all {Count} available coins as requested", selectedCoins.Count);
        }
        else
        {
            // Perform coin selection to avoid subdust change unless necessary
            selectedCoins = SelectCoins(availableCoins, totalOutput, operatorTerms, hasExplicitSubdustOutput);
        }
        
        if (selectedCoins == null || selectedCoins.Count == 0)
            throw new InvalidOperationException(
                $"Insufficient funds. Available: {availableCoins.Sum(x => x.TxOut.Value)}, Required: {totalOutput}");
        
        var totalInput = selectedCoins.Sum(x => x.TxOut.Value);
        var change =
            totalInput -
            totalOutput -
            (selectedCoins.Count * operatorTerms.FeeTerms.OffchainInput);
        
        // Add change output if it's at or above dust threshold
        if (change >= operatorTerms.Dust)
        {
            outputs = [.. outputs, new TxOut(Money.Satoshis(change), changeAddress)];
        }
        else if (change > 0UL && (hasExplicitSubdustOutput + 1) <= ArkTransactionBuilder.MaxOpReturnOutputs)
        {
            // We have subdust change - log it as it will become an OP_RETURN
            logger.LogWarning("Transaction will create subdust change of {Change} sats (< {Dust} dust threshold). " +
                            "This will be converted to an OP_RETURN output.", change, operatorTerms.Dust);
            outputs = [.. outputs, new TxOut(Money.Satoshis(change), changeAddress)];
        }

        try
        {
            return await arkTransactionBuilder.ConstructAndSubmitArkTransaction(
                selectedCoins,
                outputs,
                arkServiceClient,
                cancellationToken);
        }
        catch
        {
            var scripts = selectedCoins.Select(x => x.Contract.GetArkAddress().ScriptPubKey.ToHex())
                .Concat(
                    outputs.Select(y => y.ScriptPubKey.ToHex())).ToHashSet();

            await arkVtxoSynchronizationService.PollScriptsForVtxos(scripts.ToHashSet(), cancellationToken);
            throw;
        }
    }

    public async Task<Dictionary<string, List<SpendableArkCoinWithSigner>>> GetSpendableCoins(string[]? walletIds, bool includeRecoverable,
        CancellationToken cancellationToken)
    {
        return await GetSpendableCoins(walletIds, null, includeRecoverable, cancellationToken);
    }

    /// <summary>
    /// Get spendable coins for specified wallets, optionally filtered by specific VTXO outpoints
    /// </summary>
    /// <param name="vtxoOutpoints">Optional set of VTXO outpoints to filter by. If null, returns all spendable coins.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<Dictionary<string, List<SpendableArkCoinWithSigner>>> GetSpendableCoins(
        string[]? walletIds,
        HashSet<OutPoint>? vtxoOutpoints,
        bool includeRecoverable,
        CancellationToken cancellationToken)
    {
        // Filter VTXOs at database level for efficiency
        var vtxosAndContracts = await arkWalletService.GetVTXOsAndContracts(walletIds, false, includeRecoverable, vtxoOutpoints,null,null, cancellationToken);

        walletIds = vtxosAndContracts.Select(grouping => grouping.Key).ToArray();
        var signers = await arkadeWalletSignerProvider.GetSigners(walletIds, cancellationToken);

        var operatorTerms = await operatorTermsService.GetOperatorTerms(cancellationToken);
        var res = new Dictionary<string, List<SpendableArkCoinWithSigner>>();
        foreach (var walletSigner in signers)
        {
            var walletId = walletSigner.Key;
            var signer = walletSigner.Value;
            if (!vtxosAndContracts.TryGetValue(walletId, out var group))
                continue;
            // No need to filter again - already filtered at DB level
            var coins = await GetSpendableCoins(group, signer, operatorTerms, includeRecoverable, null, cancellationToken);
            res.Add(walletId, coins);
        }
        
        return res;
    }

    private async Task<List<SpendableArkCoinWithSigner>> GetSpendableCoins(
        Dictionary<ArkWalletContract, VTXO[]> group, IArkadeWalletSigner signer,
        ArkOperatorTerms operatorTerms, bool includeRecoverable, HashSet<OutPoint>? vtxoOutpoints = null, CancellationToken cancellationToken = default)
    {
        var coins = new List<SpendableArkCoinWithSigner>();

        foreach (var contractData in group)
        {
            var contract = ArkContract.Parse(contractData.Key.Type, contractData.Key.ContractData);
            if (contract is null)
                continue;
            foreach (var vtxo in contractData.Value)
            {
                if (vtxo.SpentByTransactionId is not null)
                    continue;
                if (!includeRecoverable && vtxo.Recoverable)
                    continue;

                // Filter by specific VTXO outpoints if provided
                if (vtxoOutpoints != null)
                {
                    var vtxoOutpoint = new OutPoint(uint256.Parse(vtxo.TransactionId), (uint)vtxo.TransactionOutputIndex);
                    if (!vtxoOutpoints.Contains(vtxoOutpoint))
                    {
                        continue;
                    }
                }

                if (!operatorTerms.SignerKey.ToBytes().SequenceEqual(contract.Server!.ToBytes()))
                {
                    continue;
                }

                var res = await GetSpendableCoin(signer, contract, vtxo.ToCoin(), vtxo.Recoverable, vtxo.ExpiresAt, vtxo.ExpiresAtHeight, cancellationToken);
                if (res is not null)
                    coins.Add(res);
            }
        }
        
        return coins;
    }

    private async Task<SpendableArkCoinWithSigner?> GetSpendableCoin(IArkadeWalletSigner signer,
        ArkContract contract, ICoinable vtxo, bool recoverable, DateTimeOffset? expiresAt, uint? expiresAtHeight, CancellationToken cancellationToken)
    {
        var user = await signer.GetXOnlyPublicKey(cancellationToken);
        switch (contract)
        {
            case ArkPaymentContract arkPaymentContract:
                if (arkPaymentContract.User.ToBytes().SequenceEqual(user.ToBytes()))
                {
                    return ToArkCoin(contract, vtxo, signer, arkPaymentContract.CollaborativePath(), null, null, null, recoverable, expiresAt, expiresAtHeight);
                }

                break;
            case HashLockedArkPaymentContract hashLockedArkPaymentContract:
                if (hashLockedArkPaymentContract.User!.ToBytes().SequenceEqual(user.ToBytes()))
                {
                    return ToArkCoin(contract, vtxo, signer,
                        hashLockedArkPaymentContract.CreateClaimScript(),
                        new WitScript(Op.GetPushOp(hashLockedArkPaymentContract.Preimage)), null, null, recoverable, expiresAt, expiresAtHeight);
                }

                break;
            case VHTLCContract htlc:
                if (htlc.Preimage is not null && htlc.Receiver.ToBytes().SequenceEqual(user.ToBytes()))
                {
                    return ToArkCoin(contract, vtxo, signer,
                        htlc.CreateClaimScript(),
                        new WitScript(Op.GetPushOp(htlc.Preimage!)), null, null, recoverable, expiresAt, expiresAtHeight);
                }
                
                var (timestamp, height) = await bitcoinTimeChainProvider.Get(cancellationToken);

                switch (htlc.RefundLocktime.IsTimeLock)
                {
                    case true:
                        if (htlc.RefundLocktime.Date <= Utils.UnixTimeToDateTime(timestamp) && htlc.Sender.ToBytes().SequenceEqual(user.ToBytes()))
                        {
                            return ToArkCoin(contract, vtxo, signer,
                                htlc.CreateRefundWithoutReceiverScript(),
                                null, htlc.RefundLocktime, null, recoverable, expiresAt, expiresAtHeight);
                        }
                        break;
                    case false:
                        if (htlc.RefundLocktime.Height <= height && htlc.Sender.ToBytes().SequenceEqual(user.ToBytes()))
                        {
                            return ToArkCoin(contract, vtxo, signer,
                                htlc.CreateRefundWithoutReceiverScript(),
                                null, htlc.RefundLocktime, null, recoverable, expiresAt, expiresAtHeight);
                        }
                        break;
                }
                break;
        }

        return null;
    }

    private static SpendableArkCoinWithSigner ToArkCoin(ArkContract c, ICoinable vtxo, IArkadeWalletSigner signer,
        ScriptBuilder leaf, WitScript? witness, LockTime? lockTime, Sequence? sequence, bool recoverable, DateTimeOffset? expiry, uint? expiryHeight)
    {
        return new SpendableArkCoinWithSigner(c, expiry, expiryHeight, vtxo.Outpoint, vtxo.TxOut, signer, leaf, witness, lockTime, sequence, recoverable);
    }

    public async Task<ArkAddress> GetDestination(ArkWallet wallet, ArkOperatorTerms arkOperatorTerms)
    {
        var (privKey, _) = await arkWalletService.GetAndIncrementLastIndexUsed(wallet.Id);
        var destination = wallet.Destination;
        destination ??= 
            ContractUtils
                .DerivePaymentContract(new DeriveContractRequest(arkOperatorTerms, privKey.CreateXOnlyPubKey()))
                .GetArkAddress();
        return destination;
    }

    /// <summary>
    /// Selects coins to minimize subdust change. Prefers exact matches or combinations that avoid subdust change.
    /// </summary>
    /// <param name="availableCoins">Available coins sorted by value descending</param>
    /// <param name="targetAmount">Target amount to send</param>
    /// <param name="dustThreshold">Dust threshold from operator terms</param>
    /// <param name="allowSubdustChange">Whether subdust change is acceptable (true if user explicitly wants subdust output)</param>
    /// <returns>Selected coins or null if impossible</returns>
    private List<SpendableArkCoinWithSigner>? SelectCoins(
        List<SpendableArkCoinWithSigner> availableCoins,
        Money targetAmount,
        ArkOperatorTerms operatorTerms,
        int currentSubDustOutputs)
    {
        if (availableCoins.Count == 0)
            return null;

        var totalAvailable = availableCoins.Sum(x => x.TxOut.Value);
        if (totalAvailable < targetAmount)
            return null;

        // Strategy 1: Try to find exact match or match with change > dust
        // Start with largest coins first (greedy approach)
        var selected = new List<SpendableArkCoinWithSigner>();
        Money currentTotal = Money.Zero;

        foreach (var coin in availableCoins)
        {
            if (currentTotal >= targetAmount)
            {
                var change = currentTotal - targetAmount;
                // Check if change is acceptable (either 0, > dust, or we can add another subdust OP_RETURN)
                var canAddSubdustChange = (currentSubDustOutputs + 1) <= ArkTransactionBuilder.MaxOpReturnOutputs;
                if (change == Money.Zero || change >= operatorTerms.Dust || canAddSubdustChange)
                    break;
            }

            selected.Add(coin);
            currentTotal += coin.TxOut.Value;
        }

        var finalChange = currentTotal - targetAmount;
        
        // If we have subdust change and can't add another OP_RETURN, try to find better combination
        var canAddSubdust = (currentSubDustOutputs + 1) <= ArkTransactionBuilder.MaxOpReturnOutputs;
        if (finalChange > Money.Zero && finalChange < operatorTerms.Dust && !canAddSubdust)
        {
            logger.LogDebug("Greedy selection resulted in subdust change ({Change} sats). Attempting to find better combination.", finalChange);
            
            // Strategy 2: Try adding one more coin to push change above dust threshold
            var remainingCoins = availableCoins.Except(selected).ToList();
            foreach (var extraCoin in remainingCoins)
            {
                var newChange = finalChange + extraCoin.TxOut.Value;
                if (newChange >= operatorTerms.Dust)
                {
                    logger.LogDebug("Adding extra coin ({CoinValue} sats) pushes change above dust threshold ({NewChange} sats).", 
                        extraCoin.TxOut.Value, newChange);
                    selected.Add(extraCoin);
                    return selected;
                }
            }
            
            logger.LogDebug("Could not push change above dust by adding single coin. Trying alternative combinations.");
            
            // Strategy 3: Try to find combination that results in no change or change > dust
            var betterSelection = TryFindBetterCombination(availableCoins, targetAmount, operatorTerms);
            if (betterSelection != null)
            {
                logger.LogDebug("Found better coin combination avoiding subdust change.");
                return betterSelection;
            }
            
            // Strategy 4: If we can't avoid subdust, use all coins to maximize change
            logger.LogDebug("Could not avoid subdust change. Using all available coins.");
            return availableCoins;
        }

        return selected;
    }

    /// <summary>
    /// Attempts to find a better coin combination that avoids subdust change
    /// </summary>
    private List<SpendableArkCoinWithSigner>? TryFindBetterCombination(
        List<SpendableArkCoinWithSigner> availableCoins,
        Money targetAmount,
        ArkOperatorTerms terms)
    {
        // Try combinations of 1-3 coins (to keep it performant)
        // Look for exact match first
        foreach (var coin in availableCoins)
        {
            if (coin.TxOut.Value == targetAmount + terms.FeeTerms.OffchainInput)
                return [coin];
        }

        // Try pairs
        for (int i = 0; i < availableCoins.Count; i++)
        {
            for (int j = i + 1; j < availableCoins.Count; j++)
            {
                var total = availableCoins[i].TxOut.Value + availableCoins[j].TxOut.Value;
                if (total < targetAmount + (2 * terms.FeeTerms.OffchainInput))
                    continue;
                    
                var change = total - (targetAmount + (2 * terms.FeeTerms.OffchainInput));
                if (change == Money.Zero || change >= terms.Dust)
                    return [availableCoins[i], availableCoins[j]];
            }
        }

        // Try triplets
        for (int i = 0; i < availableCoins.Count && i < 10; i++) // Limit to first 10 for performance
        {
            for (int j = i + 1; j < availableCoins.Count && j < 10; j++)
            {
                for (int k = j + 1; k < availableCoins.Count && k < 10; k++)
                {
                    var total = availableCoins[i].TxOut.Value + availableCoins[j].TxOut.Value + availableCoins[k].TxOut.Value;
                    if (total < targetAmount + (3 * terms.FeeTerms.OffchainInput))
                        continue;
                        
                    var change = total - (targetAmount + (3 * terms.FeeTerms.OffchainInput));
                    if (change == Money.Zero || change >= terms.Dust)
                        return [availableCoins[i], availableCoins[j], availableCoins[k]];
                }
            }
        }

        return null;
    }
}