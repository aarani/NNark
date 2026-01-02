using System.Text.Json;
using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Helpers;
using NArk.Models;
using NArk.Models.Options;
using NArk.Transactions;
using NArk.Transport;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Services;

public class IntentGenerationService(
    IClientTransport clientTransport,
    IFeeEstimator feeEstimator,
    ISigningService signingService,
    IIntentStorage intentStorage,
    IContractStorage contractStorage,
    IVtxoStorage vtxoStorage,
    IIntentScheduler intentScheduler,
    IOptions<IntentGenerationServiceOptions>? options = null
) : IIntentGenerationService, IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _generationTask;
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        var multiToken = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, cancellationToken);
        _generationTask = DoGenerationLoop(multiToken.Token);
        return Task.CompletedTask;
    }

    private async Task DoGenerationLoop(CancellationToken token)
    {
        var serverInfo = await clientTransport.GetServerInfoAsync(token);

        try
        {
            while (!token.IsCancellationRequested)
            {
                var activeContractsByWallets =
                    (await contractStorage.LoadActiveContracts(null, token))
                    .GroupBy(c => c.WalletIdentifier);

                foreach (var activeContractsByWallet in activeContractsByWallets)
                {
                    var activeContractsByScript =
                        activeContractsByWallet.GroupBy(c => c.Script)
                            .ToDictionary(g => g.Key, g => g.First());

                    var unspentVtxos =
                        await vtxoStorage.GetVtxosByScripts(
                            [.. activeContractsByScript.Keys], cancellationToken: token);

                    Dictionary<ArkCoinLite, ArkPsbtSigner> signers = [];

                    foreach (var vtxo in unspentVtxos)
                    {
                        var signer = await signingService.GetVtxoPsbtSignerByContract(activeContractsByScript[vtxo.Script], vtxo, token);
                        signers.Add(signer.Coin.ToLite(), signer);
                    }

                    var intentSpecs =
                        await intentScheduler.GetIntentsToSubmit([.. signers.Keys], token);

                    foreach (var intentSpec in intentSpecs)
                    {
                        await GenerateIntentFromSpec(activeContractsByWallet.Key, intentSpec, signers, token);
                    }
                }

                await Task.Delay(options?.Value.PollInterval ?? TimeSpan.FromMinutes(5), token);
            }
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
    }

    private async Task<Guid> GenerateIntentFromSpec(string walletId, ArkIntentSpec intentSpec, Dictionary<ArkCoinLite, ArkPsbtSigner> signers, CancellationToken token)
    {
        ArkServerInfo serverInfo = await clientTransport.GetServerInfoAsync(token);
        var outputsSum = intentSpec.Outputs.Sum(o => o.Value);
        var inputsSum = intentSpec.Coins.Sum(c => c.Amount);
        var fee = await feeEstimator.EstimateFeeAsync(intentSpec, token);
        
        if (outputsSum - inputsSum < fee)
        {
            throw new InvalidOperationException(
                $"Scheduler is not considering fees properly, missing fees by {inputsSum + fee - outputsSum} sats");
        }

        var overlappingIntents =
            await intentStorage.GetIntentsByInputs(walletId, [.. intentSpec.Coins.Select(c => c.Outpoint)], true, token);
        if (overlappingIntents.Count != 0)
            return Guid.Empty;

        var intentTxs = await CreateIntents(
            serverInfo.Network,
            [await signers[intentSpec.Coins[0]].SigningEntity.GetPublicKey(token)],
            intentSpec.ValidFrom,
            intentSpec.ValidUntil,
            [.. signers.Where(s => intentSpec.Coins.Contains(s.Key)).Select(s => s.Value)],
            intentSpec.Outputs,
            token
        );

        var internalId = Guid.NewGuid();
        await intentStorage.SaveIntent(walletId,
            new ArkIntent(internalId, null, walletId, ArkIntentState.WaitingToSubmit,
                intentSpec.ValidFrom, intentSpec.ValidUntil, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                intentTxs.RegisterTx.ToBase64(), intentTxs.RegisterMessage, intentTxs.Delete.ToBase64(),
                intentTxs.DeleteMessage, null, null, null,
                intentSpec.Coins.Select(c => c.Outpoint).ToArray(),
                (await signers[intentSpec.Coins[0]].SigningEntity.GetOutputDescriptor(token)).ToString()), token);

        return internalId;
    }

    private static async Task<PSBT> CreateIntent(string message, Network network, ArkPsbtSigner[] inputs,
        IReadOnlyCollection<TxOut>? outputs, CancellationToken cancellationToken = default)
    {
        var firstInput = inputs.First();
        var toSignTx =
            CreatePsbt(
                firstInput.Coin.ScriptPubKey,
                network,
                message,
                2U,
                0U,
                0U,
                inputs.Select(i => i.Coin).Cast<Coin>().ToArray()
            );

        var toSignGTx = toSignTx.GetGlobalTransaction();
        if (outputs is not null && outputs.Count != 0)
        {
            toSignGTx.Outputs.RemoveAt(0);
            toSignGTx.Outputs.AddRange(outputs);
        }

        inputs = [firstInput with { Coin = new ArkCoin(firstInput.Coin) }, .. inputs];
        inputs[0].Coin.TxOut = toSignTx.Inputs[0].GetTxOut();
        inputs[0].Coin.Outpoint = toSignTx.Inputs[0].PrevOut;

        var precomputedTransactionData = toSignGTx.PrecomputeTransactionData(inputs.Select(i => i.Coin.TxOut).ToArray());

        toSignTx = PSBT.FromTransaction(toSignGTx, network).UpdateFrom(toSignTx);

        foreach (var signer in inputs)
        {
            await signer.SignAndFillPsbt(toSignTx, precomputedTransactionData, cancellationToken: cancellationToken);
        }

        return toSignTx;
    }

    private static PSBT CreatePsbt(
        Script pkScript,
        Network network,
        string message,
        uint version = 0, uint lockTime = 0, uint sequence = 0, Coin[]? fundProofOutputs = null)
    {
        var messageHash = HashHelpers.CreateTaggedMessageHash("ark-intent-proof-message", message);

        var toSpend = network.CreateTransaction();
        toSpend.Version = 0;
        toSpend.LockTime = 0;
        toSpend.Inputs.Add(new TxIn(new OutPoint(uint256.Zero, 0xFFFFFFFF), new Script(OpcodeType.OP_0, Op.GetPushOp(messageHash)))
        {
            Sequence = 0,
            WitScript = WitScript.Empty,
        });
        toSpend.Outputs.Add(new TxOut(Money.Zero, pkScript));
        var toSpendTxId = toSpend.GetHash();
        var toSign = network.CreateTransaction();
        toSign.Version = version;
        toSign.LockTime = lockTime;
        toSign.Inputs.Add(new TxIn(new OutPoint(toSpendTxId, 0))
        {
            Sequence = sequence
        });

        fundProofOutputs ??= [];

        foreach (var input in fundProofOutputs)
        {
            toSign.Inputs.Add(new TxIn(input.Outpoint, Script.Empty)
            {
                Sequence = sequence,
            });
        }
        toSign.Outputs.Add(new TxOut(Money.Zero, new Script(OpcodeType.OP_RETURN)));
        var psbt = PSBT.FromTransaction(toSign, network);
        psbt.Settings.AutomaticUTXOTrimming = false;
        psbt.AddTransactions(toSpend);
        psbt.AddCoins(fundProofOutputs.Cast<ICoin>().ToArray());
        return psbt;
    }

    private static async Task<(PSBT RegisterTx, PSBT Delete, string RegisterMessage, string DeleteMessage)> CreateIntents(
        Network network,
        ECPubKey[] cosigners,
        DateTimeOffset validAt,
        DateTimeOffset expireAt,
        IReadOnlyCollection<ArkPsbtSigner> inputSigners,
        IReadOnlyCollection<ArkTxOut>? outs = null,
        CancellationToken cancellationToken = default
    )
    {
        var msg = new Messages.RegisterIntentMessage
        {
            Type = "register",
            OnchainOutputsIndexes = outs?.Select((x, i) => (x, i)).Where(o => o.x.Type == ArkTxOutType.Onchain).Select((_, i) => i).ToArray() ?? [],
            ValidAt = validAt.ToUnixTimeSeconds(),
            ExpireAt = expireAt.ToUnixTimeSeconds(),
            CosignersPublicKeys = cosigners.Select(c => Convert.ToHexStringLower(c.ToBytes())).ToArray()
        };

        var deleteMsg = new Messages.DeleteIntentMessage()
        {
            Type = "delete",
            ExpireAt = expireAt.ToUnixTimeSeconds()
        };
        var message = JsonSerializer.Serialize(msg);
        var deleteMessage = JsonSerializer.Serialize(deleteMsg);

        return (
            await CreateIntent(message, network, inputSigners.ToArray(), outs?.Cast<TxOut>().ToArray(), cancellationToken),
            await CreateIntent(deleteMessage, network, inputSigners.ToArray(), null, cancellationToken),
            message,
            deleteMessage);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdownCts.CancelAsync();

        if (_generationTask is not null)
            await _generationTask;
    }

    public async Task<Guid> GenerateManualIntent(string walletId, ArkIntentSpec spec, Dictionary<ArkCoinLite, ArkPsbtSigner> signers, CancellationToken cancellationToken)
    {
        var internalId = await GenerateIntentFromSpec(walletId, spec, signers, cancellationToken);
        
        if (internalId == Guid.Empty)
            throw new InvalidOperationException("Could not create intent, pending intents exist");
        
        return internalId;
    }
}

public interface IIntentGenerationService
{
    Task<Guid> GenerateManualIntent(string walletId, ArkIntentSpec spec, Dictionary<ArkCoinLite, ArkPsbtSigner> signers,
        CancellationToken cancellationToken);
}