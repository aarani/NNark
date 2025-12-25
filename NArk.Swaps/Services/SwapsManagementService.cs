using System.Collections.Concurrent;
using System.Threading.Channels;
using BTCPayServer.Lightning;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Services;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models.WebSocket;
using NArk.Swaps.Models;
using NArk.Transport;
using NBitcoin;

namespace NArk.Swaps.Services;

public class SwapsManagementService : IAsyncDisposable
{
    private readonly SpendingService _spendingService;
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
        _vtxoStorage = vtxoStorage;
        _wallet = wallet;
        _swapsStorage = swapsStorage;
        _contractService = contractService;
        _boltzClient = new BoltzClient(new HttpClient { BaseAddress = new Uri(boltzUrl) });
        _boltzService = new BoltzSwapService(
            _boltzClient,
            clientTransport
        );

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
            await _swapsStorage.SaveSwap(swap.WalletId,
                swap with { Status = Map(swapStatus?.Status ?? "Unknown"), UpdatedAt = DateTimeOffset.Now },
                true, cancellationToken: cancellationToken);
        }
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