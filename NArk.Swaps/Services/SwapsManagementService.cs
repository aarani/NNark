using System.Collections.Concurrent;
using System.Threading.Channels;
using BTCPayServer.Lightning;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Contracts;
using NArk.Helpers;
using NArk.Models;
using NArk.Services;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models.Swaps.Submarine;
using NArk.Swaps.Boltz.Models.WebSocket;
using NArk.Swaps.Models;
using NArk.Transactions;
using NArk.Transport;
using NBitcoin;

namespace NArk.Swaps.Services;

public class SwapsManagementService : IAsyncDisposable
{
    private readonly SpendingService _spendingService;
    private readonly IClientTransport _clientTransport;
    private readonly IVtxoStorage _vtxoStorage;
    private readonly IWallet _wallet;
    private readonly ISwapStorage _swapsStorage;
    private readonly IContractService _contractService;
    private readonly BoltzSwapService _boltzService;
    private readonly BoltzClient _boltzClient;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Channel<string> _triggerChannel = Channel.CreateUnbounded<string>();

    private HashSet<string> _swapsToWatch = [];

    private Task? _cacheTask;

    private Task? _lastStreamTask;
    private CancellationTokenSource _restartCts = new();
    private readonly TransactionHelpers.ArkTransactionBuilder _transactionBuilder;

    public SwapsManagementService(
        string boltzUrl,
        SpendingService spendingService,
        IClientTransport clientTransport,
        IVtxoStorage vtxoStorage,
        IWallet wallet,
        ISwapStorage swapsStorage,
        IContractService contractService)
    {
        _spendingService = spendingService;
        _clientTransport = clientTransport;
        _vtxoStorage = vtxoStorage;
        _wallet = wallet;
        _swapsStorage = swapsStorage;
        _contractService = contractService;
        _boltzClient = new BoltzClient(new HttpClient { BaseAddress = new Uri(boltzUrl) });
        _boltzService = new BoltzSwapService(
            _boltzClient,
            clientTransport
        );
        _transactionBuilder = new TransactionHelpers.ArkTransactionBuilder(clientTransport);

        swapsStorage.SwapsChanged += OnSwapsChanged;
        vtxoStorage.VtxosChanged += OnVtxosChanged;
    }

    private void OnSwapsChanged(object? sender, EventArgs e)
    {
        _triggerChannel.Writer.TryWrite("");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var multiToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        _cacheTask = DoUpdateStorage(multiToken.Token);
        _triggerChannel.Writer.TryWrite("");
        return Task.CompletedTask;
    }

    private async Task DoUpdateStorage(CancellationToken cancellationToken)
    {
        await foreach (var eventDetails in _triggerChannel.Reader.ReadAllAsync(cancellationToken))
        {
            if (eventDetails.StartsWith("id:"))
            {
                await PollSwapState([eventDetails[2..]], cancellationToken);
            }

            var swaps =
                await _swapsStorage.GetActiveSwaps(null, cancellationToken);
            var swapsIdSet = swaps.Select(s => s.SwapId).ToHashSet();

            if (_swapsToWatch.SetEquals(swapsIdSet))
                continue;

            await PollSwapState(swapsIdSet.Except(_swapsToWatch), cancellationToken);

            _swapsToWatch = swapsIdSet;

            await _restartCts.CancelAsync();
            _restartCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            _lastStreamTask = DoStatusCheck(swapsIdSet, _restartCts.Token);
        }
    }

    private async Task PollSwapState(IEnumerable<string> idsToPoll, CancellationToken cancellationToken)
    {
        foreach (var idToPoll in idsToPoll)
        {
            var swapStatus = await _boltzClient.GetSwapStatusAsync(idToPoll, _shutdownCts.Token);
            var swap = await _swapsStorage.GetSwap(idToPoll, cancellationToken);
            var newStatus = Map(swapStatus?.Status ?? "Unknown");
            if (swap.Status != newStatus)
            {
                var newSwap =
                    swap with { Status = newStatus, UpdatedAt = DateTimeOffset.Now };

                await _swapsStorage.SaveSwap(swap.WalletId,
                    newSwap,
                    true, cancellationToken: cancellationToken);

                if (newStatus is ArkSwapStatus.Failed && swapStatus?.Status is not null && IsRefundableStatus(swapStatus.Status))
                {
                    await RequestRefund(newSwap);
                }
            }
        }
    }

    private async Task RequestRefund(ArkSwap swap)
    {
        if (swap.SwapType != ArkSwapType.Submarine)
        {
            throw new InvalidOperationException("Only submarine swaps can be refunded");
        }

        if (swap.Status == ArkSwapStatus.Refunded)
        {
            return;
        }

        var serverInfo = await _clientTransport.GetServerInfoAsync();

        // Parse the VHTLC contract
        if (ArkContract.Parse(swap.ContractScript, serverInfo.Network) is not VHTLCContract contract)
        {
            throw new InvalidOperationException("Failed to parse VHTLC contract for refund");
        }

        // Get the wallet signer
        var signingEntity = await _wallet.FindSigningEntity(contract.Sender, CancellationToken.None);

        // Get VTXOs for this contract
        var vtxos = await _vtxoStorage.GetVtxosByScripts([swap.ContractScript], false, CancellationToken.None);
        if (vtxos.Count == 0)
        {
            // logger.LogWarning("No VTXOs found for submarine swap {SwapId} refund", swap.SwapId);
            return;
        }

        // Use the first VTXO (should only be one for a swap)
        var vtxo = vtxos.Single();

        // Get the user's wallet address for refund destination
        var refundAddress =
            await _contractService.DerivePaymentContract(swap.WalletId, CancellationToken.None);
        if (refundAddress == null)
        {
            throw new InvalidOperationException("Failed to get refund address");
        }

        // Build the refund transaction
        var refundTxBuilder = serverInfo.Network.CreateTransactionBuilder();
        refundTxBuilder.SetVersion(3);
        refundTxBuilder.SetFeeWeight(0);
        refundTxBuilder.DustPrevention = false;

        var arkCoin = contract.ToCoopRefundCoin(swap.WalletId, vtxo);

        // Add the VTXO as input
        refundTxBuilder.AddCoin(arkCoin);

        // Send full amount back to user (minus any fees handled by Ark)
        refundTxBuilder.Send(refundAddress.GetArkAddress(), Money.Satoshis(vtxo.Amount));

        var refundPsbt = refundTxBuilder.BuildPSBT(false, PSBTVersion.PSBTv0);

        var signer = new ArkPsbtSigner(arkCoin, signingEntity);

        var refundGtx = refundPsbt.GetGlobalTransaction();
        var refundPrecomputedTransactionData =
            refundGtx.PrecomputeTransactionData([signer.Coin.TxOut]);

        // Sign our part of the refund transaction
        await signer.SignAndFillPsbt(refundPsbt, refundPrecomputedTransactionData, CancellationToken.None);

        // Build checkpoint transaction (required for Ark protocol)
        var checkpointScript = serverInfo.CheckpointTapScript;
        var checkpointTxBuilder = serverInfo.Network.CreateTransactionBuilder();
        checkpointTxBuilder.SetVersion(3);
        checkpointTxBuilder.AddCoin(arkCoin);
        checkpointTxBuilder.Send(refundAddress.GetArkAddress(), Money.Satoshis(vtxo.Amount));
        var checkpointPsbt = checkpointTxBuilder.BuildPSBT(false, PSBTVersion.PSBTv0);
        checkpointPsbt.Inputs[0].WitnessUtxo = signer.Coin.TxOut;

        var checkpointGtx = checkpointPsbt.GetGlobalTransaction();
        var checkpointPrecomputedTransactionData =
            checkpointGtx.PrecomputeTransactionData([signer.Coin.TxOut]);

        // Sign checkpoint
        await signer.SignAndFillPsbt(checkpointPsbt, checkpointPrecomputedTransactionData);

        // Request Boltz to co-sign the refund
        var refundRequest = new SubmarineRefundRequest
        {
            Transaction = checkpointPsbt.ToBase64(),
            Checkpoint = checkpointPsbt.ToBase64()
        };

        var refundResponse = await _boltzClient.RefundSubmarineSwapAsync(swap.SwapId, refundRequest, CancellationToken.None);

        // Parse Boltz-signed transactions
        var boltzSignedRefundPsbt = PSBT.Parse(refundResponse.Transaction, serverInfo.Network);
        var boltzSignedCheckpointPsbt = PSBT.Parse(refundResponse.Checkpoint, serverInfo.Network);

        // Combine signatures
        refundPsbt.UpdateFrom(boltzSignedRefundPsbt);
        checkpointPsbt.UpdateFrom(boltzSignedCheckpointPsbt);

        await _transactionBuilder.SubmitArkTransaction([signer], refundPsbt, [new IndexedPSBT(checkpointPsbt, 0)],
            CancellationToken.None);

        var newSwap =
            swap with { Status = ArkSwapStatus.Refunded, UpdatedAt = DateTimeOffset.Now };
        await _swapsStorage.SaveSwap(newSwap.WalletId, newSwap, false, CancellationToken.None);

        //logger.LogInformation("Successfully refunded submarine swap {SwapId} with Ark txid {ArkTxid}", 
        //    swap.SwapId, submitResponse.ArkTxid);
    }

    private static ArkSwapStatus Map(string status)
    {
        return status switch
        {
            "swap.created" => ArkSwapStatus.Pending,
            "invoice.expired" or "swap.expired" or "transaction.failed" or "transaction.refunded" =>
                ArkSwapStatus.Failed,
            "transaction.mempool" => ArkSwapStatus.Pending,
            "transaction.confirmed" or "invoice.settled" => ArkSwapStatus.Settled,
            _ => ArkSwapStatus.Unknown
        };
    }


    private async Task DoStatusCheck(HashSet<string> swapsIds, CancellationToken cancellationToken)
    {
        await using var websocketClient = new BoltzWebsocketClient(_boltzClient.DeriveWebSocketUri());
        websocketClient.OnAnyEventReceived += OnSwapEventReceived;
        try
        {
            await websocketClient.ConnectAsync(cancellationToken);
            await websocketClient.SubscribeAsync(swapsIds.ToArray(), cancellationToken);
            await websocketClient.WaitUntilDisconnected(cancellationToken);
        }
        finally
        {
            websocketClient.OnAnyEventReceived -= OnSwapEventReceived;
        }
    }

    private Task OnSwapEventReceived(WebSocketResponse? response)
    {
        try
        {
            if (response is null)
                return Task.CompletedTask;

            if (response.Event == "update" && response is { Channel: "swap.update", Args.Count: > 0 })
            {
                var swapUpdate = response.Args[0];
                if (swapUpdate != null)
                {
                    var id = swapUpdate["id"]!.GetValue<string>();
                    _triggerChannel.Writer.TryWrite($"id:{id}");
                }
            }
        }
        catch
        {
            // ignored
        }

        return Task.CompletedTask;
    }


    public async Task<uint256> InitiateSubmarineSwap(string walletId, BOLT11PaymentRequest invoice,
        CancellationToken cancellationToken = default)
    {
        var refundEntity = await _wallet.GetNewSigningEntity(walletId, cancellationToken);
        var swap = await _boltzService.CreateSubmarineSwap(invoice,
            await refundEntity.GetOutputDescriptor(cancellationToken),
            cancellationToken);
        await _swapsStorage.SaveSwap(
            walletId,
            new ArkSwap(
                swap.Swap.Id,
                walletId,
                ArkSwapType.Submarine,
                invoice.ToString(),
                swap.Swap.ExpectedAmount,
                swap.Contract.ToString(),
                ArkSwapStatus.Pending,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                invoice.Hash.ToString()
            ), false, cancellationToken);
        try
        {
            await _contractService.ImportContract(walletId, swap.Contract, cancellationToken);
            return await _spendingService.Spend(walletId,
                [new ArkTxOut(ArkTxOutType.Vtxo, swap.Swap.ExpectedAmount, swap.Address)], cancellationToken);
        }
        catch (Exception e)
        {
            await _swapsStorage.SaveSwap(
                walletId,
                new ArkSwap(
                    swap.Swap.Id,
                    walletId,
                    ArkSwapType.Submarine,
                    invoice.ToString(),
                    swap.Swap.ExpectedAmount,
                    swap.Contract.ToString(),
                    ArkSwapStatus.Failed,
                    e.ToString(),
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    invoice.Hash.ToString()
                ), false, cancellationToken);
            throw;
        }
    }

    private void OnVtxosChanged(object? sender, EventArgs e)
    {
    }

    private bool IsRefundableStatus(string status)
    {
        // Statuses that indicate a submarine swap can be cooperatively refunded
        return status switch
        {
            "invoice.failedToPay" => true,
            "invoice.expired" => true,
            "swap.expired" => true,
            "transaction.lockupFailed" => true,
            _ => false
        };
    }

    public async ValueTask DisposeAsync()
    {
        _vtxoStorage.VtxosChanged -= OnVtxosChanged;

        await _shutdownCts.CancelAsync();

        try
        {
            if (_cacheTask is not null)
                await _cacheTask;
        }
        catch
        {
            // ignored
        }

        try
        {
            if (_lastStreamTask is not null)
                await _lastStreamTask;
        }
        catch
        {
            // ignored
        }
    }
}