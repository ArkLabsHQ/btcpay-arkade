// using AsyncKeyedLock;
// using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
// using BTCPayServer.Plugins.ArkPayServer.Storage;
// using Microsoft.Extensions.Logging;
// using NArk;
// using NArk.Abstractions;
// using NArk.Abstractions.Blockchain;
// using NArk.Abstractions.Wallets;
// using ArkWallet = BTCPayServer.Plugins.ArkPayServer.Data.Entities.ArkWallet;
// using NArk.Contracts;
// using NArk.Scripts;
// using NArk.Services;
// using NArk.Transactions;
// using NArk.Transport;
// using NArk.Swaps.Helpers;
// using NBitcoin;
// using NBitcoin.Scripting;
//
// namespace BTCPayServer.Plugins.ArkPayServer.Services;
//
// public class ArkadeSpender(
//     AsyncKeyedLocker asyncKeyedLocker,
//     IArkadeMultiWalletSigner arkadeWalletSignerProvider,
//     ISpendingService spendingService,
//     IContractService contractService,
//     EfCoreVtxoStorage vtxoStorage,
//     ILogger<ArkadeSpender> logger,
//     IClientTransport clientTransport,
//     VtxoPollingService vtxoPollingService,
//     IChainTimeProvider bitcoinTimeChainProvider)
// {
//     public async Task<uint256> Spend(string walletId, TxOut[] outputs, CancellationToken cancellationToken = default)
//     {
//         // Convert TxOut[] to ArkTxOut[] for NNark API
//         var arkOutputs = outputs.Select(o =>
//             new ArkTxOut(ArkTxOutType.Vtxo, o.Value, o.ScriptPubKey.GetDestination()!)).ToArray();
//
//         // Use NNark's SpendingService which handles coin selection, change, and subdust internally
//         return await spendingService.Spend(walletId, arkOutputs, cancellationToken);
//     }
//
//     public async Task<uint256> Spend(ArkWallet wallet, IEnumerable<ArkPsbtSigner> coins, TxOut[] outputs,
//         CancellationToken cancellationToken = default)
//     {
//         return await Spend(wallet, coins, outputs, true, cancellationToken);
//     }
//
//     public async Task<uint256> Spend(ArkWallet wallet, IEnumerable<ArkPsbtSigner> coins, TxOut[] outputs,
//         bool useAllCoins, CancellationToken cancellationToken = default)
//     {
//         using var l = await asyncKeyedLocker.LockAsync($"ark-{wallet.Id}-txs-spending", cancellationToken);
//         var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
//         var destination = await GetDestination(wallet);
//         return await SpendWalletCoins(coins, serverInfo, outputs, destination, useAllCoins, cancellationToken);
//     }
//
//     private async Task<uint256> SpendWalletCoins(IEnumerable<ArkPsbtSigner> coins,
//         ArkServerInfo serverInfo, TxOut[] outputs, ArkAddress changeAddress, bool useAllCoins, CancellationToken cancellationToken)
//     {
//         var totalOutput = outputs.Sum(x => x.Value);
//         var availableCoins = coins.OrderByDescending(x => x.Coin.TxOut.Value).ToList();
//
//         // Check if any output is explicitly subdust (user wants to send subdust amount)
//         var hasExplicitSubdustOutput = outputs.Count(o => o.Value < serverInfo.Dust);
//
//         // Select coins based on useAllCoins parameter
//         List<ArkPsbtSigner> selectedCoins;
//         if (useAllCoins)
//         {
//             // Use all available coins
//             selectedCoins = availableCoins;
//             logger.LogInformation("Using all {Count} available coins as requested", selectedCoins.Count);
//         }
//         else
//         {
//             // Perform coin selection to avoid subdust change unless necessary
//             selectedCoins = SelectCoins(availableCoins, totalOutput, serverInfo.Dust, hasExplicitSubdustOutput);
//         }
//
//         if (selectedCoins == null || selectedCoins.Count == 0)
//             throw new InvalidOperationException(
//                 $"Insufficient funds. Available: {availableCoins.Sum(x => x.Coin.TxOut.Value)}, Required: {totalOutput}");
//
//         var totalInput = selectedCoins.Sum(x => x.Coin.TxOut.Value);
//         var change = totalInput - totalOutput;
//
//         // Add change output if it's at or above dust threshold
//         if (change >= serverInfo.Dust)
//         {
//             outputs = outputs.Concat([new TxOut(Money.Satoshis(change), changeAddress)]).ToArray();
//         }
//         else if (change > 0 && (hasExplicitSubdustOutput + 1) <= 3) // Max 3 OP_RETURN outputs
//         {
//             // We have subdust change - log it as it will become an OP_RETURN
//             logger.LogWarning("Transaction will create subdust change of {Change} sats (< {Dust} dust threshold). " +
//                             "This will be converted to an OP_RETURN output.", change, serverInfo.Dust);
//             outputs = outputs.Concat([new TxOut(Money.Satoshis(change), changeAddress)]).ToArray();
//         }
//
//         try
//         {
//             // Convert TxOut[] to ArkTxOut[] for the new NNark API
//             // ArkTxOut requires IDestination, so we convert ScriptPubKey to a KeyId if possible, or use directly
//             var arkOutputs = outputs.Select(o =>
//                 new ArkTxOut(ArkTxOutType.Vtxo, o.Value, o.ScriptPubKey.GetDestination()!)).ToArray();
//             var walletId = selectedCoins.First().Coin.WalletIdentifier;
//             return await spendingService.Spend(walletId, selectedCoins.ToArray(), arkOutputs, cancellationToken);
//         }
//         catch (Exception ex)
//         {
//             var scripts = selectedCoins.Select(x => x.Coin.Contract.GetArkAddress().ScriptPubKey.ToHex())
//                 .Concat(
//                     outputs.Select(y => y.ScriptPubKey.ToHex())).ToHashSet();
//
//             await vtxoPollingService.PollScriptsForVtxos(scripts.ToHashSet(), cancellationToken);
//             throw;
//         }
//     }
//
//     public async Task<Dictionary<string, List<ArkPsbtSigner>>> GetSpendableCoins(string[]? walletIds, bool includeRecoverable,
//         CancellationToken cancellationToken)
//     {
//         return await GetSpendableCoins(walletIds, null, includeRecoverable, cancellationToken);
//     }
//
//     /// <summary>
//     /// Get spendable coins for specified wallets, optionally filtered by specific VTXO outpoints
//     /// </summary>
//     /// <param name="vtxoOutpoints">Optional set of VTXO outpoints to filter by. If null, returns all spendable coins.</param>
//     /// <param name="cancellationToken">Cancellation token</param>
//     public async Task<Dictionary<string, List<ArkPsbtSigner>>> GetSpendableCoins(
//         string[]? walletIds,
//         HashSet<OutPoint>? vtxoOutpoints,
//         bool includeRecoverable,
//         CancellationToken cancellationToken)
//     {
//         
//         // Get contracts for the wallets
//         var contracts = await contractStorage.GetContractsForWalletsAsync(
//             walletIds,
//             searchText: null,
//             active: null,
//             includeSwaps: false,
//             cancellationToken);
//
//         // Build the wallet-contract-vtxos structure
//         var vtxosAndContracts = new Dictionary<string, Dictionary<ArkWalletContract, VTXO[]>>();
//
//         foreach (var contract in contracts)
//         {
//             // Get VTXOs for this contract
//             var contractVtxos = await vtxoStorage.GetVtxosByScriptsAndOutpointsAsync(
//                 [contract.Script],
//                 vtxoOutpoints,
//                 includeSpent: false,
//                 includeRecoverable,
//                 cancellationToken);
//
//             if (!contractVtxos.Any())
//                 continue;
//
//             if (!vtxosAndContracts.TryGetValue(contract.WalletId, out var walletContracts))
//             {
//                 walletContracts = new Dictionary<ArkWalletContract, VTXO[]>();
//                 vtxosAndContracts[contract.WalletId] = walletContracts;
//             }
//
//             walletContracts[contract] = contractVtxos.ToArray();
//         }
//
//         var effectiveWalletIds = vtxosAndContracts.Keys.ToArray();
//         var signers = await arkadeWalletSignerProvider.CreateSigners(effectiveWalletIds, cancellationToken);
//
//         var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
//         var res = new Dictionary<string, List<ArkPsbtSigner>>();
//         foreach (var walletSigner in signers)
//         {
//             var walletId = walletSigner.Key;
//             var signer = walletSigner.Value;
//             if (!vtxosAndContracts.TryGetValue(walletId, out var group))
//                 continue;
//             // No need to filter again - already filtered at DB level
//             var coins = await GetSpendableCoins(group, signer, serverInfo, false, null, cancellationToken);
//             res.Add(walletId, coins);
//         }
//
//         return res;
//     }
//
//     private async Task<List<ArkPsbtSigner>> GetSpendableCoins(
//         Dictionary<ArkWalletContract, VTXO[]> group, ISigningEntity signer,
//         ArkServerInfo serverInfo, bool includeRecoverable, HashSet<OutPoint>? vtxoOutpoints = null, CancellationToken cancellationToken = default)
//     {
//         var coins = new List<ArkPsbtSigner>();
//         var signerDescriptor = await signer.GetOutputDescriptor(cancellationToken);
//
//         foreach (var contractData in group)
//         {
//             var contract = ArkContract.Parse(contractData.Key.Type, contractData.Key.ContractData, serverInfo.Network);
//             if (contract is null)
//                 continue;
//             var walletId = contractData.Key.WalletId;
//
//             foreach (var vtxo in contractData.Value)
//             {
//                 if (vtxo.SpentByTransactionId is not null)
//                     continue;
//                 if (!includeRecoverable && vtxo.Recoverable)
//                     continue;
//
//                 // Filter by specific VTXO outpoints if provided
//                 if (vtxoOutpoints != null)
//                 {
//                     var vtxoOutpoint = new OutPoint(uint256.Parse(vtxo.TransactionId), (uint)vtxo.TransactionOutputIndex);
//                     if (!vtxoOutpoints.Contains(vtxoOutpoint))
//                     {
//                         continue;
//                     }
//                 }
//
//                 // Compare server descriptors by extracting XOnlyPubKey from both
//                 var serverPubKey = OutputDescriptorHelpers.Extract(serverInfo.SignerKey).XOnlyPubKey;
//                 var contractServerPubKey = OutputDescriptorHelpers.Extract(contract.Server).XOnlyPubKey;
//                 if (!serverPubKey.ToBytes().SequenceEqual(contractServerPubKey.ToBytes()))
//                 {
//                     continue;
//                 }
//
//                 var res = await GetSpendableCoin(walletId, signer, signerDescriptor, contract, vtxo, cancellationToken);
//                 if (res is not null)
//                     coins.Add(res);
//             }
//         }
//
//         return coins;
//     }
//
//     private async Task<ArkPsbtSigner?> GetSpendableCoin(
//         string walletId,
//         ISigningEntity signer,
//         OutputDescriptor signerDescriptor,
//         ArkContract contract,
//         VTXO vtxo,
//         CancellationToken cancellationToken)
//     {
//         var userKey = await signer.GetPublicKey(cancellationToken);
//         var user = userKey.ToXOnlyPubKey();
//         var coin = vtxo.ToCoin();
//
//         switch (contract)
//         {
//             case ArkPaymentContract arkPaymentContract:
//                 var arkPaymentUserKey = OutputDescriptorHelpers.Extract(arkPaymentContract.User).XOnlyPubKey;
//                 if (arkPaymentUserKey.ToBytes().SequenceEqual(user.ToBytes()))
//                 {
//                     return ToArkPsbtSigner(walletId, contract, vtxo, signerDescriptor, signer,
//                         arkPaymentContract.CollaborativePath(), null, null, null);
//                 }
//                 break;
//
//             case HashLockedArkPaymentContract hashLockedArkPaymentContract:
//                 var hashLockedUserKey = OutputDescriptorHelpers.Extract(hashLockedArkPaymentContract.User!).XOnlyPubKey;
//                 if (hashLockedUserKey.ToBytes().SequenceEqual(user.ToBytes()))
//                 {
//                     return ToArkPsbtSigner(walletId, contract, vtxo, signerDescriptor, signer,
//                         hashLockedArkPaymentContract.CreateClaimScript(),
//                         new WitScript(Op.GetPushOp(hashLockedArkPaymentContract.Preimage)), null, null);
//                 }
//                 break;
//
//             case VHTLCContract htlc:
//                 var htlcReceiverKey = OutputDescriptorHelpers.Extract(htlc.Receiver).XOnlyPubKey;
//                 var htlcSenderKey = OutputDescriptorHelpers.Extract(htlc.Sender).XOnlyPubKey;
//                 if (htlc.Preimage is not null && htlcReceiverKey.ToBytes().SequenceEqual(user.ToBytes()))
//                 {
//                     return ToArkPsbtSigner(walletId, contract, vtxo, signerDescriptor, signer,
//                         htlc.CreateClaimScript(),
//                         new WitScript(Op.GetPushOp(htlc.Preimage!)), null, null);
//                 }
//
//                 var (timestamp, height) = await bitcoinTimeChainProvider.GetChainTime(cancellationToken);
//
//                 switch (htlc.RefundLocktime.IsTimeLock)
//                 {
//                     case true:
//                         if (htlc.RefundLocktime.Date <= timestamp && htlcSenderKey.ToBytes().SequenceEqual(user.ToBytes()))
//                         {
//                             return ToArkPsbtSigner(walletId, contract, vtxo, signerDescriptor, signer,
//                                 htlc.CreateRefundWithoutReceiverScript(),
//                                 null, htlc.RefundLocktime, null);
//                         }
//                         break;
//                     case false:
//                         if (htlc.RefundLocktime.Height <= height && htlcSenderKey.ToBytes().SequenceEqual(user.ToBytes()))
//                         {
//                             return ToArkPsbtSigner(walletId, contract, vtxo, signerDescriptor, signer,
//                                 htlc.CreateRefundWithoutReceiverScript(),
//                                 null, htlc.RefundLocktime, null);
//                         }
//                         break;
//                 }
//                 break;
//         }
//
//         return null;
//     }
//
//     private static ArkPsbtSigner ToArkPsbtSigner(
//         string walletId,
//         ArkContract contract,
//         VTXO vtxo,
//         OutputDescriptor signerDescriptor,
//         ISigningEntity signer,
//         ScriptBuilder spendingScript,
//         WitScript? spendingWitness,
//         LockTime? lockTime,
//         Sequence? sequence)
//     {
//         var outpoint = new OutPoint(uint256.Parse(vtxo.TransactionId), (uint)vtxo.TransactionOutputIndex);
//         var txOut = new TxOut(Money.Satoshis(vtxo.Amount), Script.FromHex(vtxo.Script));
//
//         var coin = new ArkCoin(
//             walletIdentifier: walletId,
//             contract: contract,
//             birth: vtxo.SeenAt,
//             expiresAt: vtxo.ExpiresAt,
//             expiresAtHeight: vtxo.ExpiresAtHeight,
//             outPoint: outpoint,
//             txOut: txOut,
//             signerDescriptor: signerDescriptor,
//             spendingScriptBuilder: spendingScript,
//             spendingConditionWitness: spendingWitness,
//             lockTime: lockTime,
//             sequence: sequence,
//             recoverable: vtxo.Recoverable
//         );
//         return new ArkPsbtSigner(coin, signer);
//     }
//
//     public async Task<ArkAddress> GetDestination(ArkWallet wallet)
//     {
//         // If wallet has an explicit destination set, use it
//         if (!string.IsNullOrEmpty(wallet.WalletDestination))
//         {
//             return ArkAddress.Parse(wallet.WalletDestination);
//         }
//
//         var contract =  await contractService.DerivePaymentContract(wallet.Id);
//         return contract.GetArkAddress();
//     }
//
//     /// <summary>
//     /// Selects coins to minimize subdust change. Prefers exact matches or combinations that avoid subdust change.
//     /// </summary>
//     /// <param name="availableCoins">Available coins sorted by value descending</param>
//     /// <param name="targetAmount">Target amount to send</param>
//     /// <param name="dustThreshold">Dust threshold from operator terms</param>
//     /// <param name="currentSubDustOutputs">Current number of subdust outputs</param>
//     /// <returns>Selected coins or null if impossible</returns>
//     private List<ArkPsbtSigner>? SelectCoins(
//         List<ArkPsbtSigner> availableCoins,
//         Money targetAmount,
//         Money dustThreshold,
//         int currentSubDustOutputs)
//     {
//         if (availableCoins.Count == 0)
//             return null;
//
//         var totalAvailable = availableCoins.Sum(x => x.Coin.TxOut.Value);
//         if (totalAvailable < targetAmount)
//             return null;
//
//         // Strategy 1: Try to find exact match or match with change > dust
//         // Start with largest coins first (greedy approach)
//         var selected = new List<ArkPsbtSigner>();
//         Money currentTotal = Money.Zero;
//
//         foreach (var coin in availableCoins)
//         {
//             if (currentTotal >= targetAmount)
//             {
//                 var change = currentTotal - targetAmount;
//                 // Check if change is acceptable (either 0, > dust, or we can add another subdust OP_RETURN)
//                 var canAddSubdustChange = (currentSubDustOutputs + 1) <= 3; // Max 3 OP_RETURN outputs
//                 if (change == Money.Zero || change >= dustThreshold || canAddSubdustChange)
//                     break;
//             }
//
//             selected.Add(coin);
//             currentTotal += coin.Coin.TxOut.Value;
//         }
//
//         var finalChange = currentTotal - targetAmount;
//
//         // If we have subdust change and can't add another OP_RETURN, try to find better combination
//         var canAddSubdust = (currentSubDustOutputs + 1) <= 3; // Max 3 OP_RETURN outputs
//         if (finalChange > Money.Zero && finalChange < dustThreshold && !canAddSubdust)
//         {
//             logger.LogDebug("Greedy selection resulted in subdust change ({Change} sats). Attempting to find better combination.", finalChange);
//
//             // Strategy 2: Try adding one more coin to push change above dust threshold
//             var remainingCoins = availableCoins.Except(selected).ToList();
//             foreach (var extraCoin in remainingCoins)
//             {
//                 var newChange = finalChange + extraCoin.Coin.TxOut.Value;
//                 if (newChange >= dustThreshold)
//                 {
//                     logger.LogDebug("Adding extra coin ({CoinValue} sats) pushes change above dust threshold ({NewChange} sats).",
//                         extraCoin.Coin.TxOut.Value, newChange);
//                     selected.Add(extraCoin);
//                     return selected;
//                 }
//             }
//
//             logger.LogDebug("Could not push change above dust by adding single coin. Trying alternative combinations.");
//
//             // Strategy 3: Try to find combination that results in no change or change > dust
//             var betterSelection = TryFindBetterCombination(availableCoins, targetAmount, dustThreshold);
//             if (betterSelection != null)
//             {
//                 logger.LogDebug("Found better coin combination avoiding subdust change.");
//                 return betterSelection;
//             }
//
//             // Strategy 4: If we can't avoid subdust, use all coins to maximize change
//             logger.LogDebug("Could not avoid subdust change. Using all available coins.");
//             return availableCoins;
//         }
//
//         return selected;
//     }
//
//     /// <summary>
//     /// Attempts to find a better coin combination that avoids subdust change
//     /// </summary>
//     private List<ArkPsbtSigner>? TryFindBetterCombination(
//         List<ArkPsbtSigner> availableCoins,
//         Money targetAmount,
//         Money dustThreshold)
//     {
//         // Try combinations of 1-3 coins (to keep it performant)
//         // Look for exact match first
//         foreach (var coin in availableCoins)
//         {
//             if (coin.Coin.TxOut.Value == targetAmount)
//                 return [coin];
//         }
//
//         // Try pairs
//         for (int i = 0; i < availableCoins.Count; i++)
//         {
//             for (int j = i + 1; j < availableCoins.Count; j++)
//             {
//                 var total = availableCoins[i].Coin.TxOut.Value + availableCoins[j].Coin.TxOut.Value;
//                 if (total < targetAmount)
//                     continue;
//
//                 var change = total - targetAmount;
//                 if (change == Money.Zero || change >= dustThreshold)
//                     return [availableCoins[i], availableCoins[j]];
//             }
//         }
//
//         // Try triplets
//         for (int i = 0; i < availableCoins.Count && i < 10; i++) // Limit to first 10 for performance
//         {
//             for (int j = i + 1; j < availableCoins.Count && j < 10; j++)
//             {
//                 for (int k = j + 1; k < availableCoins.Count && k < 10; k++)
//                 {
//                     var total = availableCoins[i].Coin.TxOut.Value + availableCoins[j].Coin.TxOut.Value + availableCoins[k].Coin.TxOut.Value;
//                     if (total < targetAmount)
//                         continue;
//
//                     var change = total - targetAmount;
//                     if (change == Money.Zero || change >= dustThreshold)
//                         return [availableCoins[i], availableCoins[j], availableCoins[k]];
//                 }
//             }
//         }
//
//         return null;
//     }
// }