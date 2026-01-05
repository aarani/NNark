using System.Collections.Concurrent;
using System.Threading.Channels;
using BTCPayServer.Lightning;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Contracts;
using NArk.Helpers;
using NArk.Services;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models.Swaps.Submarine;
using NArk.Swaps.Boltz.Models.WebSocket;
using NArk.Swaps.Helpers;
using NArk.Swaps.Models;
using NArk.Transactions;
using NArk.Transport;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Swaps.Services;

public class SwapsManagementService : IAsyncDisposable
{
    private readonly SpendingService _spendingService;
    private readonly IClientTransport _clientTransport;
    private readonly IVtxoStorage _vtxoStorage;
    private readonly IWallet _wallet;
    private readonly ISwapStorage _swapsStorage;
    private readonly IContractService _contractService;
    private readonly IContractStorage _contractStorage;
    private readonly ISafetyService _safetyService;
    private readonly BoltzSwapService _boltzService;
    private readonly BoltzClient _boltzClient;
    private readonly TransactionHelpers.ArkTransactionBuilder _transactionBuilder;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Channel<string> _triggerChannel = Channel.CreateUnbounded<string>();

    private HashSet<string> _swapsIdToWatch = [];
    private ConcurrentDictionary<string, string> _swapAddressToIds = [];

    private Task? _cacheTask;

    private Task? _lastStreamTask;
    private CancellationTokenSource _restartCts = new();
    private Network? _network;
    private ECXOnlyPubKey? _serverKey;

    public SwapsManagementService(
        SpendingService spendingService,
        IClientTransport clientTransport,
        IVtxoStorage vtxoStorage,
        IWallet wallet,
        ISwapStorage swapsStorage,
        IContractService contractService,
        IContractStorage contractStorage,
        ISafetyService safetyService,
        IIntentStorage intentStorage,
        BoltzClient boltzClient
    )
    {
        _spendingService = spendingService;
        _clientTransport = clientTransport;
        _vtxoStorage = vtxoStorage;
        _wallet = wallet;
        _swapsStorage = swapsStorage;
        _contractService = contractService;
        _contractStorage = contractStorage;
        _safetyService = safetyService;
        _boltzClient = boltzClient;
        _boltzService = new BoltzSwapService(
            _boltzClient,
            _clientTransport
        );
        _transactionBuilder =
            new TransactionHelpers.ArkTransactionBuilder(clientTransport, safetyService, intentStorage);

        swapsStorage.SwapsChanged += OnSwapsChanged;
        // It is possible to listen for vtxos on scripts and use them to figure out the state of swaps
        vtxoStorage.VtxosChanged += OnVtxosChanged;
    }

    private void OnVtxosChanged(object? sender, ArkVtxo e)
    {
        if (_network is null || _serverKey is null) return;

        try
        {
            var vtxoAddress = ArkAddress
                .FromScriptPubKey(Script.FromHex(e.Script), _serverKey)
                .ToString(_network.ChainName == ChainName.Mainnet);
            if (_swapAddressToIds.TryGetValue(vtxoAddress, out var id))
            {
                _triggerChannel.Writer.TryWrite($"id:{id}");
            }
        }
        catch
        {
            // ignored
        }
    }

    private void OnSwapsChanged(object? sender, ArkSwap swapChanged)
    {
        _triggerChannel.Writer.TryWrite($"id:{swapChanged.SwapId}");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var multiToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);

        var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);
        _serverKey = serverInfo.SignerKey.Extract().XOnlyPubKey;
        _network = serverInfo.Network;

        _cacheTask = DoUpdateStorage(multiToken.Token);
        _triggerChannel.Writer.TryWrite("");
    }

    private async Task DoUpdateStorage(CancellationToken cancellationToken)
    {
        await foreach (var eventDetails in _triggerChannel.Reader.ReadAllAsync(cancellationToken))
        {
            if (eventDetails.StartsWith("id:"))
            {
                var swapId = eventDetails[3..];

                // If we already monitor this swap, no need to restart websocket
                if (_swapsIdToWatch.Contains(swapId))
                {
                    await PollSwapState([swapId], cancellationToken);
                }
                else
                {
                    await PollSwapState([swapId], cancellationToken);

                    HashSet<string> newSwapIdSet = [.. _swapsIdToWatch, swapId];
                    _swapsIdToWatch = newSwapIdSet;

                    var newRestartCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
                    _lastStreamTask = DoStatusCheck(newSwapIdSet, newRestartCts.Token);
                    await _restartCts.CancelAsync();
                    _restartCts = newRestartCts;

                }
            }
            else
            {
                var swaps =
                    await _swapsStorage.GetActiveSwaps(null, cancellationToken);
                var newSwapIdSet =
                    swaps.Select(s => s.SwapId).ToHashSet();

                if (_swapsIdToWatch.SetEquals(newSwapIdSet))
                    continue;

                await PollSwapState(newSwapIdSet.Except(_swapsIdToWatch), cancellationToken);

                _swapsIdToWatch = newSwapIdSet;

                var newRestartCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
                _lastStreamTask = DoStatusCheck(newSwapIdSet, newRestartCts.Token);
                await _restartCts.CancelAsync();
                _restartCts = newRestartCts;
            }
        }
    }

    private async Task PollSwapState(IEnumerable<string> idsToPoll, CancellationToken cancellationToken)
    {
        foreach (var idToPoll in idsToPoll)
        {
            var swapStatus = await _boltzClient.GetSwapStatusAsync(idToPoll, _shutdownCts.Token);
            if (swapStatus?.Status is null) continue;

            await using var @lock = await _safetyService.LockKeyAsync($"swap::{idToPoll}", cancellationToken);
            var swap = await _swapsStorage.GetSwap(idToPoll, cancellationToken);
            _swapAddressToIds[swap.SwapId] = swap.Address;

            // There's nothing after refunded, ignore...
            if (swap.Status is ArkSwapStatus.Refunded) continue;

            // If not refunded and status is refundable, start a coop refund
            if (swap.Status is not ArkSwapStatus.Refunded && IsRefundableStatus(swapStatus.Status))
            {
                var newSwap =
                    swap with { Status = ArkSwapStatus.Failed, UpdatedAt = DateTimeOffset.Now };
                await RequestRefund(newSwap);
            }

            var newStatus = Map(swapStatus.Status);

            if (swap.Status == newStatus) continue;

            var swapWithNewStatus =
                swap with { Status = newStatus, UpdatedAt = DateTimeOffset.Now };

            await _swapsStorage.SaveSwap(swap.WalletId,
                swapWithNewStatus, cancellationToken: cancellationToken);

            if (swapWithNewStatus.Status is ArkSwapStatus.Settled or ArkSwapStatus.Refunded)
            {
                _swapAddressToIds.Remove(swapWithNewStatus.SwapId, out _);
                _swapsIdToWatch.Remove(swapWithNewStatus.SwapId);
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
        var vtxos = await _vtxoStorage.GetVtxosByScripts([contract.GetArkAddress().ScriptPubKey.ToHex()], false,
            CancellationToken.None);
        if (vtxos.Count == 0)
        {
            // logger.LogWarning("No VTXOs found for submarine swap {SwapId} refund", swap.SwapId);
            return;
        }

        // Use the first VTXO (should only be one for a swap)
        var vtxo = vtxos.Single();

        if (vtxo.Recoverable)
            throw new NotImplementedException("Recoverable scenario is not implemented");

        // Get the user's wallet address for refund destination
        var refundAddress =
            await _contractService.DerivePaymentContract(swap.WalletId, CancellationToken.None);
        if (refundAddress == null)
        {
            throw new InvalidOperationException("Failed to get refund address");
        }

        var arkCoin = contract.ToCoopRefundCoin(swap.WalletId, vtxo);

        var signer = new ArkPsbtSigner(arkCoin, signingEntity);

        var (arkTx, checkpoints) =
            await _transactionBuilder.ConstructArkTransaction([signer],
                [new ArkTxOut(ArkTxOutType.Vtxo, arkCoin.Amount, refundAddress.GetArkAddress())],
                serverInfo, CancellationToken.None);

        var checkpoint = checkpoints.Single();

        // Request Boltz to co-sign the refund
        var refundRequest = new SubmarineRefundRequest
        {
            Transaction = arkTx.ToBase64(),
            Checkpoint = checkpoint.Psbt.ToBase64()
        };

        var refundResponse =
            await _boltzClient.RefundSubmarineSwapAsync(swap.SwapId, refundRequest, CancellationToken.None);

        // Parse Boltz-signed transactions
        var boltzSignedRefundPsbt = PSBT.Parse(refundResponse.Transaction, serverInfo.Network);
        var boltzSignedCheckpointPsbt = PSBT.Parse(refundResponse.Checkpoint, serverInfo.Network);

        // Combine signatures
        arkTx.UpdateFrom(boltzSignedRefundPsbt);
        checkpoint.Psbt.UpdateFrom(boltzSignedCheckpointPsbt);

        await _transactionBuilder.SubmitArkTransaction([signer], arkTx, [checkpoint],
            CancellationToken.None);

        var newSwap =
            swap with { Status = ArkSwapStatus.Refunded, UpdatedAt = DateTimeOffset.Now };

        await _swapsStorage.SaveSwap(newSwap.WalletId, newSwap, CancellationToken.None);

        await using var @lock = await _safetyService.LockKeyAsync($"contract::{contract.GetArkAddress().ScriptPubKey.ToHex()}", CancellationToken.None);
        var responsibleContract = await _contractStorage.LoadContractsByScripts([contract.GetArkAddress().ScriptPubKey.ToHex()], CancellationToken.None);

        foreach (var arkContractEntity in responsibleContract)
        {
            await _contractStorage.SaveContract(arkContractEntity.WalletIdentifier,
                arkContractEntity with { Important = false }, CancellationToken.None);
        }
    }

    private static ArkSwapStatus Map(string status)
    {
        return status switch
        {
            "swap.created" or "invoice.set" => ArkSwapStatus.Pending,
            "invoice.failedToPay" or "invoice.expired" or "swap.expired" or "transaction.failed"
                or "transaction.refunded" =>
                ArkSwapStatus.Failed,
            "transaction.mempool" => ArkSwapStatus.Pending,
            "transaction.confirmed" or "invoice.settled" or "transaction.claimed" => ArkSwapStatus.Settled,
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


    public async Task<string> InitiateSubmarineSwap(string walletId, BOLT11PaymentRequest invoice, bool autoPay = true,
        CancellationToken cancellationToken = default)
    {
        var serverInfo = await _clientTransport.GetServerInfoAsync(cancellationToken);
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
                swap.Address.ToString(serverInfo.Network.ChainName == ChainName.Mainnet),
                ArkSwapStatus.Pending,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                invoice.Hash.ToString()
            ), cancellationToken);
        try
        {
            await _contractService.ImportContract(walletId, swap.Contract, cancellationToken);
            return autoPay
                ? (await _spendingService.Spend(walletId,
                    [new ArkTxOut(ArkTxOutType.Vtxo, swap.Swap.ExpectedAmount, swap.Address)], cancellationToken))
                .ToString()
                : swap.Swap.Id;
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
                    swap.Address.ToString(serverInfo.Network.ChainName == ChainName.Mainnet),
                    ArkSwapStatus.Failed,
                    e.ToString(),
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    invoice.Hash.ToString()
                ), cancellationToken);
            throw;
        }
    }

    public async Task<uint256> PayExistingSubmarineSwap(string walletId, string swapId,
        CancellationToken cancellationToken = default)
    {
        var swap = await _swapsStorage.GetSwap(swapId, cancellationToken);
        try
        {
            return await _spendingService.Spend(walletId,
                [new ArkTxOut(ArkTxOutType.Vtxo, swap.ExpectedAmount, ArkAddress.Parse(swap.Address))],
                cancellationToken);
        }
        catch (Exception e)
        {
            await _swapsStorage.SaveSwap(
                walletId,
                swap with
                {
                    Status = ArkSwapStatus.Failed,
                    FailReason = e.ToString(),
                    UpdatedAt = DateTimeOffset.UtcNow
                }, cancellationToken);
            throw;
        }
    }

    public async Task<string> InitiateReverseSwap(string walletId, CreateInvoiceParams invoiceParams,
        CancellationToken cancellationToken = default)
    {
        var destinationEntity = await _wallet.GetNewSigningEntity(walletId, cancellationToken);
        var revSwap =
            await _boltzService.CreateReverseSwap(
                invoiceParams,
                await destinationEntity.GetOutputDescriptor(cancellationToken),
                cancellationToken
            );
        await _contractService.ImportContract(walletId, revSwap.Contract, cancellationToken);
        await _swapsStorage.SaveSwap(
            walletId,
            new ArkSwap(
                revSwap.Swap.Id,
                walletId,
                ArkSwapType.ReverseSubmarine,
                revSwap.Swap.Invoice,
                (long)invoiceParams.Amount.ToUnit(LightMoneyUnit.Satoshi),
                revSwap.Contract.ToString(),
                revSwap.Swap.LockupAddress,
                ArkSwapStatus.Pending,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                new uint256(revSwap.Hash).ToString()
            ), cancellationToken);

        return revSwap.Swap.Invoice;
    }

    private static bool IsRefundableStatus(string status)
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
        _swapsStorage.SwapsChanged -= OnSwapsChanged;

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