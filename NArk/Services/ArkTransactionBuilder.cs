using Ark.V1;
using Microsoft.Extensions.Logging;
using NArk.Contracts;
using NArk.Models;
using NArk.Scripts;
using NArk.Services.Abstractions;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Services
{

    /// <summary>
    /// Utility class for building and constructing Ark transactions
    /// </summary>
    public class ArkTransactionBuilder(
        IOperatorTermsService operatorTermsService,
        ILogger<ArkTransactionBuilder> logger)
    {
        /// <summary>
        /// Maximum number of OP_RETURN outputs allowed per transaction
        /// </summary>
        public const int MaxOpReturnOutputs = 1;

        public async Task<PSBT> FinalizeCheckpointTx(PSBT checkpointTx, PSBT receivedCheckpointTx, SpendableArkCoinWithSigner coin, CancellationToken cancellationToken)
        {
            logger.LogDebug("Finalizing checkpoint transaction for coin with outpoint {Outpoint}", coin.Outpoint);
            // Sign the checkpoint transaction
            var checkpointGtx = receivedCheckpointTx.GetGlobalTransaction();
            var checkpointPrecomputedTransactionData =
                checkpointGtx.PrecomputeTransactionData([coin.TxOut]);

            receivedCheckpointTx.UpdateFrom(checkpointTx);
            await coin.SignAndFillPSBT(receivedCheckpointTx, checkpointPrecomputedTransactionData, cancellationToken: cancellationToken);

            return receivedCheckpointTx;
        }


        public async Task<uint256> ConstructAndSubmitArkTransaction(
            IEnumerable<SpendableArkCoinWithSigner> arkCoins,
            TxOut[] arkOutputs,
            Ark.V1.ArkService.ArkServiceClient arkServiceClient,
            CancellationToken cancellationToken)
        {
            var (arkTx, checkpoints) = await ConstructArkTransaction(arkCoins, arkOutputs, cancellationToken);
            await SubmitArkTransaction(arkCoins, arkServiceClient, arkTx, checkpoints, cancellationToken);
            return arkTx.GetGlobalTransaction().GetHash();
        }

        public async Task<PSBT> ConstructForfeitTx(SpendableArkCoinWithSigner coin, Coin? connector, IDestination forfeitDestination, CancellationToken cancellationToken = default)
        {
            logger.LogDebug("Constructing forfeit transaction for coin {Outpoint}", coin.Outpoint);
            
            var terms = await operatorTermsService.GetOperatorTerms(cancellationToken);
            var p2a = Script.FromHex("51024e73"); // Standard Ark protocol marker
            
            // Determine sighash based on whether we have a connector
            // Without connector: ANYONECANPAY|ALL (allows adding connector later)
            // With connector: DEFAULT (signs all inputs)
            var sighash = connector is null 
                ? TaprootSigHash.AnyoneCanPay | TaprootSigHash.All 
                : TaprootSigHash.Default;
            
            // Build forfeit transaction
            var txBuilder = terms.Network.CreateTransactionBuilder();
            txBuilder.SetVersion(3);
            txBuilder.SetFeeWeight(0);
            txBuilder.DustPrevention = false;
            txBuilder.ShuffleInputs = false;
            txBuilder.ShuffleOutputs = false;
            txBuilder.SetLockTime(coin.SpendingLockTime ?? LockTime.Zero);
            
            // Add VTXO input
            txBuilder.AddCoin(coin, new CoinOptions()
            {
                Sequence = coin.SpendingSequence
            });
            
            // Add connector input if provided
            if (connector is not null)
            {
                txBuilder.AddCoin(connector);
            }
            
            // Calculate total input amount based on connector + input OR assumed connector amount (dust)
            var totalInput = coin.Amount + (connector?.Amount ?? terms.Dust);
            
            // Send to forfeit destination (operator's forfeit address)
            txBuilder.Send(forfeitDestination, totalInput);
            
            // Add P2A output
            var forfeitTx = txBuilder.BuildPSBT(false, PSBTVersion.PSBTv0);
            var gtx = forfeitTx.GetGlobalTransaction();
            gtx.Outputs.Add(new TxOut(Money.Zero, p2a));
            forfeitTx = PSBT.FromTransaction(gtx, terms.Network, PSBTVersion.PSBTv0);
            txBuilder.UpdatePSBT(forfeitTx);
            
            // Sign the VTXO input with the appropriate sighash
            var coins = connector is not null 
                ? new[] { coin.TxOut, connector.TxOut } 
                : new[] { coin.TxOut };
            
            //sort the checkpoint coins based on the input index in arkTx

            var sortedCheckpointCoins = new Dictionary<int, TxOut>();
            foreach (var input in forfeitTx.Inputs)
            {
                sortedCheckpointCoins.Add((int)input.Index, coins.Single(x => x.ScriptPubKey == input.GetTxOut().ScriptPubKey));
            }

            // Sign each input in the Ark transaction
            var precomputedTransactionData =
                gtx.PrecomputeTransactionData(sortedCheckpointCoins.OrderBy(x => x.Key).Select(x=>x.Value).ToArray());
            
            await coin.SignAndFillPSBT(forfeitTx, precomputedTransactionData, sighash, cancellationToken);
            
            logger.LogInformation("Forfeit transaction constructed successfully for coin {Outpoint}", coin.Outpoint);
            
            return forfeitTx;
        }

        /// <summary>
        /// Completes an existing forfeit PSBT by adding a connector coin.
        /// The existing forfeit must have the VTXO input signed with ANYONECANPAY|ALL sighash.
        /// This method adds the connector coin and signs it with DEFAULT sighash.
        /// </summary>
        public async Task<PSBT> CompleteForfeitTx(PSBT existingForfeit, Coin connector, IArkadeWalletSigner delegateSigner,OutputDescriptor delegateOutputDescriptor, CancellationToken cancellationToken = default)
        {
            logger.LogDebug("Completing forfeit transaction by adding connector {ConnectorOutpoint}", connector.Outpoint);
            
            var terms = await operatorTermsService.GetOperatorTerms(cancellationToken);
            
            // Parse the existing transaction
            var gtx = existingForfeit.GetGlobalTransaction();
            
            // Add connector input
            gtx.Inputs.Add(new TxIn(connector.Outpoint));

            var completedForfeit = PSBT.FromTransaction(gtx, existingForfeit.Network).UpdateFrom(existingForfeit);
            
            // Add connector coin to PSBT
            var connectorInput = completedForfeit.Inputs.FindIndexedInput(connector.Outpoint);
            if (connectorInput == null)
            {
                throw new InvalidOperationException($"Could not find connector input {connector.Outpoint} in PSBT");
            }
            connectorInput.WitnessUtxo = connector.TxOut;
            
            var vtxo = completedForfeit.Inputs.FindIndexedInput(existingForfeit.Inputs[0].PrevOut)!;
            
            // Sign the connector input with DEFAULT sighash
            var precomputedTransactionData = completedForfeit.PrecomputeTransactionData();

            var leafScript = vtxo.GetTaprootLeafScript()[0];
            
            var hash = gtx.GetSignatureHashTaproot(precomputedTransactionData,
                new TaprootExecutionData((int)vtxo.Index, leafScript.leafScript.LeafHash));
            
            var (signature, pubKey) = await delegateSigner.Sign(hash,delegateOutputDescriptor, cancellationToken);
            
            vtxo.SetTaprootScriptSpendSignature(pubKey, leafScript.leafScript.LeafHash, signature);
           
            logger.LogInformation("Existing Forfeit transaction finished successfully for coin {Outpoint}", vtxo.PrevOut);
            
            return completedForfeit;
        }

        public async Task<Ark.V1.FinalizeTxResponse> SubmitArkTransaction(
            IEnumerable<SpendableArkCoinWithSigner> arkCoins,
            Ark.V1.ArkService.ArkServiceClient arkServiceClient,
            PSBT arkTx,
            SortedSet<IndexedPSBT> checkpoints,
            CancellationToken cancellationToken)
        {
            var network = arkTx.Network;

            logger.LogInformation($"Submitting Ark transaction with {checkpoints.Count} checkpoints: \nArkTx: {arkTx.GetGlobalTransaction().GetHash()}\nCheckpoints: {string.Join("\n", checkpoints.Select(x => x.PSBT.GetGlobalTransaction().GetHash()))}");

            // Submit the transaction
            var submitRequest = new Ark.V1.SubmitTxRequest
            {
                SignedArkTx = arkTx.ToBase64(),
                CheckpointTxs = { checkpoints.Select(x => x.PSBT.ToBase64()) }
            };
            SubmitTxResponse? response;
            try
            {
                logger.LogDebug("Sending SubmitTx request to Ark service");
                response = await arkServiceClient.SubmitTxAsync(submitRequest, cancellationToken: cancellationToken);
                logger.LogDebug("Received SubmitTx response with Ark txid: {ArkTxid}", response.ArkTxid);
            }
            catch (Exception e)
            {
                logger.LogError(e, $"Error submitting transaction\n{submitRequest}");
                throw;
            }



            // Process the signed checkpoints from the server
            var parsedReceivedCheckpoints = response.SignedCheckpointTxs
                .Select(x => PSBT.Parse(x, network))
                .ToDictionary(psbt => psbt.GetGlobalTransaction().GetHash());

            // Combine client and server signatures
            logger.LogDebug("Combining client and server signatures for {CheckpointsCount} checkpoints",
                checkpoints.Count);

            SortedSet<IndexedPSBT> signedCheckpoints = [];
            foreach (var signedCheckpoint in checkpoints)
            {
                var coin = arkCoins.Single(x => x.Outpoint == signedCheckpoint.PSBT.Inputs.Single().PrevOut);

                var psbt = await FinalizeCheckpointTx(signedCheckpoint.PSBT, parsedReceivedCheckpoints[signedCheckpoint.PSBT.GetGlobalTransaction().GetHash()], coin, cancellationToken);

                signedCheckpoints.Add(new IndexedPSBT(psbt, signedCheckpoint.Index));
            }

            // Finalize the transaction
            var finalizeTxRequest = new Ark.V1.FinalizeTxRequest
            {
                ArkTxid = response.ArkTxid,
                FinalCheckpointTxs = { signedCheckpoints.Select(x => x.PSBT.ToBase64()) }
            };
            try
            {
                logger.LogDebug("Sending FinalizeTx request to Ark service");
                var finalizeResponse =
                    await arkServiceClient.FinalizeTxAsync(finalizeTxRequest, cancellationToken: cancellationToken);
                logger.LogInformation("Transaction finalized successfully. Ark txid: {ArkTxid}", response.ArkTxid);

                return finalizeResponse;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error finalizing transaction {finalizeTxRequest}", finalizeTxRequest);
                throw;
            }
        }


        /// <summary>
        /// Constructs an Ark transaction with checkpoint transactions for each input
        /// </summary>
        /// <param name="coins">Collection of coins and their respective signers</param>
        /// <param name="outputs">Output transactions</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The Ark transaction and checkpoint transactions with their input witnesses</returns>
        public async Task<(PSBT arkTx, SortedSet<IndexedPSBT> checkpoints)> ConstructArkTransaction(
            IEnumerable<SpendableArkCoinWithSigner> coins,
            TxOut[] outputs,
            CancellationToken cancellationToken)
        {
            logger.LogInformation("Constructing Ark transaction with {CoinsCount} coins and {OutputsCount} outputs",
                coins.Count(), outputs.Length);

            var p2a = Script.FromHex("51024e73"); // Standard Ark protocol marker

            List<PSBT> checkpoints = new();
            List<SpendableArkCoinWithSigner> checkpointCoins = new();
            var terms = await operatorTermsService.GetOperatorTerms(cancellationToken);
            foreach (var coin in coins)
            {
                logger.LogDebug("Creating checkpoint for coin with outpoint {Outpoint}", coin.Outpoint);

                // Create checkpoint contract
                var checkpointContract = CreateCheckpointContract(coin, terms);

                // Build checkpoint transaction
                var checkpoint = terms.Network.CreateTransactionBuilder();
                checkpoint.SetVersion(3);
                checkpoint.SetFeeWeight(0);
                checkpoint.AddCoin(coin, new CoinOptions()
                {
                    Sequence = coin.SpendingSequence
                });
                checkpoint.DustPrevention = false;
                checkpoint.Send(checkpointContract.GetArkAddress(), coin.Amount);
                checkpoint.SetLockTime(coin.SpendingLockTime ?? LockTime.Zero);
                var checkpointTx = checkpoint.BuildPSBT(false, PSBTVersion.PSBTv0);

                //checkpoints MUST have the p2a output at index 1 and NBitcoin tx builder does not assure it, so we hack our way there
                var ctx = checkpointTx.GetGlobalTransaction();
                ctx.Outputs.Add(new TxOut(Money.Zero, p2a));
                checkpointTx = PSBT.FromTransaction(ctx, terms.Network, PSBTVersion.PSBTv0);
                checkpoint.UpdatePSBT(checkpointTx);

                _ = coin.FillPSBTInput(checkpointTx);
                checkpoints.Add(checkpointTx);

                // Create checkpoint coin for the Ark transaction
                var txout = checkpointTx.Outputs.Single(output =>
                    output.ScriptPubKey == checkpointContract.GetArkAddress().ScriptPubKey);
                var outpoint = new OutPoint(checkpointTx.GetGlobalTransaction(), txout.Index);

                checkpointCoins.Add(
                    new SpendableArkCoinWithSigner(
                        checkpointContract,
                        coin.ExpiresAt,
                        coin.ExpiresAtHeight,
                        outpoint,
                        txout.GetTxOut()!,
                        coin.Signer,
                        coin.SignerDescriptor,
                        coin.SpendingScriptBuilder,
                        coin.SpendingConditionWitness,
                        coin.SpendingLockTime,
                        coin.SpendingSequence,
                        coin.Recoverable
                    )
                );
            }

            // Build the Ark transaction that spends from all checkpoint outputs
            logger.LogDebug("Building Ark transaction with {CheckpointCount} checkpoint coins", checkpointCoins.Count);
            var arkTx = terms.Network.CreateTransactionBuilder();
            arkTx.SetVersion(3);
            arkTx.SetFeeWeight(0);
            arkTx.DustPrevention = false;
            // arkTx.Send(p2a, Money.Zero);
            arkTx.AddCoins(checkpointCoins);

            // Track OP_RETURN outputs to enforce the limit
            // First, count any existing OP_RETURN outputs
            int opReturnCount = outputs.Count(o => o.ScriptPubKey.IsUnspendable);
            
            if (opReturnCount > MaxOpReturnOutputs)
            {
                throw new InvalidOperationException(
                    $"Transaction already contains {opReturnCount} OP_RETURN outputs, which exceeds the maximum of {MaxOpReturnOutputs}.");
            }
            
            foreach (var output in outputs)
            {
                // Check if this is an Ark address output that needs subdust handling
                var scriptPubKey = output.ScriptPubKey;
                
                // If the output value is below dust threshold and it's a P2TR output,
                // convert it to an OP_RETURN output
                if (output.Value < terms.Dust && PayToTaprootTemplate.Instance.CheckScriptPubKey(scriptPubKey))
                {
                    if (opReturnCount >= MaxOpReturnOutputs)
                    {
                        throw new InvalidOperationException(
                            $"Cannot create more than {MaxOpReturnOutputs} OP_RETURN outputs per transaction. " +
                            $"Output with value {output.Value} is below dust threshold {terms.Dust}. " +
                            $"Transaction already contains {opReturnCount} OP_RETURN output(s).");
                    }
                    
                    // Extract the taproot pubkey and create OP_RETURN script
                    var taprootPubKey = PayToTaprootTemplate.Instance.ExtractScriptPubKeyParameters(scriptPubKey);
                    scriptPubKey = new Script(OpcodeType.OP_RETURN, Op.GetPushOp(taprootPubKey.ToBytes()));
                    opReturnCount++;
                    
                    logger.LogDebug("Converting sub-dust output ({Value} < {Dust}) to OP_RETURN script", 
                        output.Value, terms.Dust);
                }
                
                arkTx.Send(scriptPubKey, output.Value);
            }

            var tx = arkTx.BuildPSBT(false, PSBTVersion.PSBTv0);
            var gtx = tx.GetGlobalTransaction();
            gtx.Outputs.Add(new TxOut(Money.Zero, p2a));
            tx = PSBT.FromTransaction(gtx, terms.Network, PSBTVersion.PSBTv0);
            arkTx.UpdatePSBT(tx);



            //sort the checkpoint coins based on the input index in arkTx

            var sortedCheckpointCoins = new Dictionary<int, SpendableArkCoinWithSigner>();
            foreach (var input in tx.Inputs)
            {
                sortedCheckpointCoins.Add((int)input.Index, checkpointCoins.Single(x => x.Outpoint == input.PrevOut));
            }

            // Sign each input in the Ark transaction
            var precomputedTransactionData =
                gtx.PrecomputeTransactionData(sortedCheckpointCoins.OrderBy(x => x.Key).Select(x => x.Value.TxOut).ToArray());


            foreach (var (inputIndex, coin) in sortedCheckpointCoins)
            {
                logger.LogDebug($"Signing Ark transaction for input {inputIndex}");

                await coin.SignAndFillPSBT(tx, precomputedTransactionData, cancellationToken: cancellationToken);

            }

            logger.LogInformation("Ark transaction construction completed successfully");
            //reorder the checkpoints to match the order of the inputs of the Ark transaction

            return (tx, new SortedSet<IndexedPSBT>(checkpoints.Select(psbt =>
            {
                var output = psbt.Outputs.Single(output => output.ScriptPubKey != p2a);
                var outpoint = new OutPoint(psbt.GetGlobalTransaction(), output.Index);
                var index = tx.Inputs.FindIndexedInput(outpoint)!.Index;
                return new IndexedPSBT(psbt, (int)index);
            })));
        }

        /// <summary>
        /// Creates a checkpoint contract based on the input contract type
        /// </summary>
        private ArkContract CreateCheckpointContract(SpendableArkCoinWithSigner coin, ArkOperatorTerms terms)
        {
            if (coin.Contract.Server is null)
                throw new ArgumentException("Server key is required for checkpoint contract creation");

           
            var scriptBuilders = new List<ScriptBuilder>
            {
                coin.SpendingScriptBuilder,
                terms.CheckpointTapscript
            };

            return new GenericArkContract(coin.Contract.Server, scriptBuilders);

        }
    }
}
