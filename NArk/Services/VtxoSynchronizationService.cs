using System.Threading.Channels;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Transport;

namespace NArk.Services;

public class VtxoSynchronizationService : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _queryTask;

    private CancellationTokenSource? _restartCts;
    private Task? _streamTask;

    private HashSet<string> _lastViewOfScripts = [];

    private readonly SemaphoreSlim _viewSyncLock = new(1);

    private readonly Channel<HashSet<string>> _readyToPoll =
        Channel.CreateBounded<HashSet<string>>(new BoundedChannelOptions(5));

    private readonly IWalletStorage _walletStorage;
    private readonly IVtxoStorage _vtxoStorage;
    private readonly IContractStorage _contractStorage;
    private readonly IClientTransport _arkClientTransport;

    public VtxoSynchronizationService(IWalletStorage walletStorage,
        IVtxoStorage vtxoStorage,
        IContractStorage contractStorage,
        IClientTransport arkClientTransport)
    {
        _walletStorage = walletStorage;
        _vtxoStorage = vtxoStorage;
        _contractStorage = contractStorage;
        _arkClientTransport = arkClientTransport;

        _contractStorage.ContractsChanged += OnContractsChanged;
        _vtxoStorage.VtxosChanged += OnVtxosChanged;
    }

    private async void OnVtxosChanged(object? sender, ArkVtxo v)
    {
        try
        {
            await UpdateScriptsView(_shutdownCts.Token);
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch
        {
            // ignored
        }
    }

    private async void OnContractsChanged(object? sender, ArkContractEntity e)
    {
        try
        {
            await UpdateScriptsView(_shutdownCts.Token);
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch
        {
            // ignored
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var multiToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        _queryTask = StartQueryLogic(multiToken.Token);
        await UpdateScriptsView(multiToken.Token);
    }

    private async Task UpdateScriptsView(CancellationToken token)
    {
        await _viewSyncLock.WaitAsync(token);
        try
        {
            var wallets = await _walletStorage.LoadAllWallets(token);
            HashSet<string> newViewOfScripts = [];
            foreach (var wallet in wallets)
            {
                var contracts = await _contractStorage.LoadAllContractsByWallet(wallet.WalletIdentifier, token);
                newViewOfScripts.UnionWith(contracts.Select(c => c.Script));
            }

            foreach (var vtxo in await _vtxoStorage.GetUnspentVtxos(token))
            {
                newViewOfScripts.Add(vtxo.Script);
            }

            if (newViewOfScripts.Count == 0)
                return;

            // We already have a stream with this exact script list
            if (newViewOfScripts.SetEquals(_lastViewOfScripts) && _streamTask is not null && !_streamTask.IsCompleted)
                return;

            try
            {
                if (_restartCts is not null)
                    await _restartCts.CancelAsync();
                if (_streamTask is not null)
                    await _streamTask;
            }
            catch
            {
                // ignored
            }

            _lastViewOfScripts = newViewOfScripts;
            _restartCts = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdownCts.Token);
            // Start a new subscription stream
            _streamTask = StartStreamLogic(newViewOfScripts, _restartCts.Token);
            // Do an initial poll of all scripts
            await _readyToPoll.Writer.WriteAsync(newViewOfScripts, token);
        }
        finally
        {
            _viewSyncLock.Release();
        }
    }

    private async Task StartStreamLogic(HashSet<string> scripts, CancellationToken token)
    {
        try
        {
            var restartableToken =
                CancellationTokenSource.CreateLinkedTokenSource(token, _shutdownCts.Token);
            await foreach (var vtxosToPoll in _arkClientTransport.GetVtxoToPollAsStream(scripts, restartableToken.Token))
            {
                await _readyToPoll.Writer.WriteAsync(vtxosToPoll, restartableToken.Token);
            }
        }
        catch when (!token.IsCancellationRequested)
        {
            await UpdateScriptsView(_shutdownCts.Token);
        }
        catch
        {
            // ignored
        }
    }

    private async Task? StartQueryLogic(CancellationToken cancellationToken)
    {
        await foreach (var pollBatch in _readyToPoll.Reader.ReadAllAsync(cancellationToken))
        {
            await foreach (var vtxo in _arkClientTransport.GetVtxoByScriptsAsSnapshot(pollBatch, cancellationToken))
            {
                // Upsert
                await _vtxoStorage.SaveVtxo(vtxo, cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdownCts.CancelAsync();

        try
        {
            if (_queryTask is not null)
                await _queryTask;
        }
        catch (OperationCanceledException)
        {

        }
        try
        {
            if (_streamTask is not null)
                await _streamTask;
        }
        catch (OperationCanceledException)
        {

        }

    }
}