using NArk.Abstractions;
using NArk.Contracts;
using NArk.Models;
using NArk.Scripts;
using NArk.Transactions;
using NArk.Transport;
using NBitcoin;

namespace NArk.Helpers;

public static class TransactionHelpers
{
    public const int MaxOpReturnOutputs = 1;

    /// <summary>
    /// Utility class for building and constructing Ark transactions
    /// </summary>
    public class ArkTransactionBuilder(
        IClientTransport clientTransport)
    {
        private async Task<PSBT> FinalizeCheckpointTx(PSBT checkpointTx, PSBT receivedCheckpointTx, ArkPsbtSigner coin, CancellationToken cancellationToken)
        {
            // Sign the checkpoint transaction
            var checkpointGtx = receivedCheckpointTx.GetGlobalTransaction();
            var checkpointPrecomputedTransactionData =
                checkpointGtx.PrecomputeTransactionData([coin.Coin.TxOut]);

            receivedCheckpointTx.UpdateFrom(checkpointTx);
            await coin.SignAndFillPsbt(receivedCheckpointTx, checkpointPrecomputedTransactionData, cancellationToken);

            return receivedCheckpointTx;
        }

        /// <summary>
        /// Constructs an Ark transaction with checkpoint transactions for each input
        /// </summary>
        /// <param name="coins">Collection of coins and their respective signers</param>
        /// <param name="outputs">Output transactions</param>
        /// <param name="serverInfo">Info retrieved from Ark operator</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The Ark transaction and checkpoint transactions with their input witnesses</returns>
        public async Task<(PSBT arkTx, SortedSet<IndexedPSBT> checkpoints)> ConstructArkTransaction(
            IEnumerable<ArkPsbtSigner> coins,
            TxOut[] outputs,
            ArkServerInfo serverInfo,
            CancellationToken cancellationToken)
        {
            var p2A = Script.FromHex("51024e73"); // Standard Ark protocol marker

            List<PSBT> checkpoints = [];
            List<ArkPsbtSigner> checkpointCoins = [];
            foreach (var coin in coins)
            {
                // Create a checkpoint contract
                var checkpointContract = CreateCheckpointContract(coin, serverInfo.CheckpointTapScript);

                // Build checkpoint transaction
                var checkpoint = serverInfo.Network.CreateTransactionBuilder();
                checkpoint.SetVersion(3);
                checkpoint.SetFeeWeight(0);
                checkpoint.AddCoin(coin.Coin, new CoinOptions()
                {
                    Sequence = coin.Coin.Sequence
                });
                checkpoint.DustPrevention = false;
                checkpoint.Send(checkpointContract.GetArkAddress(), coin.Coin.Amount);
                checkpoint.SetLockTime(coin.Coin.LockTime ?? LockTime.Zero);
                var checkpointTx = checkpoint.BuildPSBT(false, PSBTVersion.PSBTv0);

                //checkpoints MUST have the p2a output at index '1' and NBitcoin tx builder does not assure it, so we hack our way there
                var ctx = checkpointTx.GetGlobalTransaction();
                ctx.Outputs.Add(new TxOut(Money.Zero, p2A));
                checkpointTx = PSBT.FromTransaction(ctx, serverInfo.Network, PSBTVersion.PSBTv0);
                checkpoint.UpdatePSBT(checkpointTx);

                _ = coin.Coin.FillPsbtInput(checkpointTx);
                checkpoints.Add(checkpointTx);

                // Create a checkpoint coin for the Ark transaction
                var txout = checkpointTx.Outputs.Single(output =>
                    output.ScriptPubKey == checkpointContract.GetArkAddress().ScriptPubKey);
                var outpoint = new OutPoint(checkpointTx.GetGlobalTransaction(), txout.Index);

                checkpointCoins.Add(
                    coin with
                    {
                        Coin = new ArkCoin(
                        coin.Coin.WalletIdentifier,
                        checkpointContract,
                        coin.Coin.ExpiresAt,
                        coin.Coin.ExpiresAtHeight,
                        outpoint,
                        txout.GetTxOut()!,
                        coin.Coin.SignerDescriptor,
                        coin.Coin.SpendingScriptBuilder,
                        coin.Coin.SpendingConditionWitness,
                        coin.Coin.LockTime,
                        coin.Coin.Sequence,
                        coin.Coin.Recoverable
                    )
                    }
                );
            }

            // Build the Ark transaction that spends from all checkpoint outputs

            var arkTx = serverInfo.Network.CreateTransactionBuilder();
            arkTx.SetVersion(3);
            arkTx.SetFeeWeight(0);
            arkTx.DustPrevention = false;
            // arkTx.Send(p2a, Money.Zero);
            arkTx.AddCoins(checkpointCoins.Select(c => c.Coin));

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

                // If the output value is below the dust threshold, and it's a P2TR output,
                // convert it to an OP_RETURN output
                if (output.Value < serverInfo.Dust && PayToTaprootTemplate.Instance.CheckScriptPubKey(scriptPubKey))
                {
                    if (opReturnCount >= MaxOpReturnOutputs)
                    {
                        throw new InvalidOperationException(
                            $"Cannot create more than {MaxOpReturnOutputs} OP_RETURN outputs per transaction. " +
                            $"Output with value {output.Value} is below dust threshold {serverInfo.Dust}. " +
                            $"Transaction already contains {opReturnCount} OP_RETURN output(s).");
                    }

                    // Extract the taproot pubkey and create an OP_RETURN script
                    var taprootPubKey = PayToTaprootTemplate.Instance.ExtractScriptPubKeyParameters(scriptPubKey);
                    if (taprootPubKey is null)
                        throw new FormatException("BUG: Could not extract Taproot parameters from scriptPubKey");
                    scriptPubKey = new Script(OpcodeType.OP_RETURN, Op.GetPushOp(taprootPubKey.ToBytes()));
                    opReturnCount++;

                }

                arkTx.Send(scriptPubKey, output.Value);
            }

            var tx = arkTx.BuildPSBT(false, PSBTVersion.PSBTv0);
            var gtx = tx.GetGlobalTransaction();
            gtx.Outputs.Add(new TxOut(Money.Zero, p2A));
            tx = PSBT.FromTransaction(gtx, serverInfo.Network, PSBTVersion.PSBTv0);
            arkTx.UpdatePSBT(tx);

            //sort the checkpoint coins based on the input index in arkTx

            var sortedCheckpointCoins =
                tx.Inputs.ToDictionary(input => (int)input.Index, input => checkpointCoins.Single(x => x.Coin.Outpoint == input.PrevOut));

            // Sign each input in the Ark transaction
            var precomputedTransactionData =
                gtx.PrecomputeTransactionData(sortedCheckpointCoins.OrderBy(x => x.Key).Select(x => x.Value.Coin.TxOut).ToArray());


            foreach (var (_, coin) in sortedCheckpointCoins)
            {
                await coin.SignAndFillPsbt(tx, precomputedTransactionData, cancellationToken);
            }

            //reorder the checkpoints to match the order of the inputs of the Ark transaction

            return (tx, new SortedSet<IndexedPSBT>(checkpoints.Select(psbt =>
            {
                var output = psbt.Outputs.Single(output => output.ScriptPubKey != p2A);
                var outpoint = new OutPoint(psbt.GetGlobalTransaction(), output.Index);
                var index = tx.Inputs.FindIndexedInput(outpoint)!.Index;
                return new IndexedPSBT(psbt, (int)index);
            })));
        }

        /// <summary>
        /// Creates a checkpoint contract based on the input contract type
        /// </summary>
        private ArkContract CreateCheckpointContract(ArkPsbtSigner coin, UnilateralPathArkTapScript serverUnrollScript)
        {
            if (coin.Coin.Contract.Server is null)
                throw new ArgumentException("Server key is required for checkpoint contract creation");


            var scriptBuilders = new List<ScriptBuilder>
            {
                coin.Coin.SpendingScriptBuilder,
                serverUnrollScript
            };

            return new GenericArkContract(coin.Coin.Contract.Server, scriptBuilders);
        }

        public async Task SubmitArkTransaction(
            IReadOnlyCollection<ArkPsbtSigner> arkCoins,
            PSBT arkTx,
            SortedSet<IndexedPSBT> checkpoints,
            CancellationToken cancellationToken
        )
        {
            var network = arkTx.Network;

            var response = await clientTransport.SubmitTx(arkTx.ToBase64(), [.. checkpoints.Select(c => c.Psbt.ToBase64())], cancellationToken);

            // Process the signed checkpoints from the server
            var parsedReceivedCheckpoints = response.SignedCheckpointTxs
                .Select(x => PSBT.Parse(x, network))
                .ToDictionary(psbt => psbt.GetGlobalTransaction().GetHash());

            SortedSet<IndexedPSBT> signedCheckpoints = [];
            foreach (var signedCheckpoint in checkpoints)
            {
                var coin = arkCoins.Single(x => x.Coin.Outpoint == signedCheckpoint.Psbt.Inputs.Single().PrevOut);
                var psbt = await FinalizeCheckpointTx(signedCheckpoint.Psbt, parsedReceivedCheckpoints[signedCheckpoint.Psbt.GetGlobalTransaction().GetHash()], coin, cancellationToken);
                signedCheckpoints.Add(signedCheckpoint with { Psbt = psbt });
            }

            await clientTransport.FinalizeTx(response.ArkTxId, [.. signedCheckpoints.Select(x => x.Psbt.ToBase64())], cancellationToken: cancellationToken);
        }


        public async Task<uint256> ConstructAndSubmitArkTransaction(
            IReadOnlyCollection<ArkPsbtSigner> arkCoins,
            ArkTxOut[] arkOutputs,
            CancellationToken cancellationToken)
        {
            if (arkOutputs.Any(o => o.Type is not ArkTxOutType.Vtxo))
                throw new InvalidOperationException();
            var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
            var (arkTx, checkpoints) = await ConstructArkTransaction(arkCoins, [.. arkOutputs], serverInfo, cancellationToken);
            await SubmitArkTransaction(arkCoins, arkTx, checkpoints, cancellationToken);
            return arkTx.GetGlobalTransaction().GetHash();
        }

        public async Task<PSBT> ConstructForfeitTx(ArkServerInfo arkServerInfo, ArkPsbtSigner signer, Coin? connector, IDestination forfeitDestination, CancellationToken cancellationToken = default)
        {
            var coin = signer.Coin;
            var p2a = Script.FromHex("51024e73"); // Standard Ark protocol marker

            // Determine sighash based on whether we have a connector
            // Without connector: ANYONECANPAY|ALL (allows adding connector later)
            // With connector: DEFAULT (signs all inputs)
            var sighash = connector is null
                ? TaprootSigHash.AnyoneCanPay | TaprootSigHash.All
                : TaprootSigHash.Default;

            // Build forfeit transaction
            var txBuilder = arkServerInfo.Network.CreateTransactionBuilder();
            txBuilder.SetVersion(3);
            txBuilder.SetFeeWeight(0);
            txBuilder.DustPrevention = false;
            txBuilder.ShuffleInputs = false;
            txBuilder.ShuffleOutputs = false;
            txBuilder.SetLockTime(coin.LockTime ?? LockTime.Zero);

            // Add VTXO input
            txBuilder.AddCoin(coin, new CoinOptions()
            {
                Sequence = coin.Sequence
            });

            // Add connector input if provided
            if (connector is not null)
            {
                txBuilder.AddCoin(connector);
            }

            // Calculate total input amount based on connector + input OR assumed connector amount (dust)
            var totalInput = coin.Amount + (connector?.Amount ?? arkServerInfo.Dust);

            // Send to forfeit destination (operator's forfeit address)
            txBuilder.Send(forfeitDestination, totalInput);

            // Add P2A output
            var forfeitTx = txBuilder.BuildPSBT(false, PSBTVersion.PSBTv0);
            var gtx = forfeitTx.GetGlobalTransaction();
            gtx.Outputs.Add(new TxOut(Money.Zero, p2a));
            forfeitTx = PSBT.FromTransaction(gtx, arkServerInfo.Network, PSBTVersion.PSBTv0);
            txBuilder.UpdatePSBT(forfeitTx);

            // Sign the VTXO input with the appropriate sighash
            var coins = connector is not null
                ? new[] { coin.TxOut, connector.TxOut }
                : new[] { coin.TxOut };

            //sort the checkpoint coins based on the input index in arkTx

            var sortedCheckpointCoins =
                forfeitTx
                    .Inputs
                    .ToDictionary(input => (int)input.Index, input => coins.Single(x => x.ScriptPubKey == input.GetTxOut()?.ScriptPubKey));

            // Sign each input in the Ark transaction
            var precomputedTransactionData =
                gtx.PrecomputeTransactionData(sortedCheckpointCoins.OrderBy(x => x.Key).Select(x => x.Value).ToArray());

            await signer.SignAndFillPsbt(forfeitTx, precomputedTransactionData, cancellationToken, sighash);

            return forfeitTx;
        }
    }
}