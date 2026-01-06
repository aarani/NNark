using System.Threading.Channels;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<VtxoSynchronizationService>? _logger;

    public VtxoSynchronizationService(IWalletStorage walletStorage,
        IVtxoStorage vtxoStorage,
        IContractStorage contractStorage,
        IClientTransport arkClientTransport,
        ILogger<VtxoSynchronizationService> logger)
        : this(walletStorage, vtxoStorage, contractStorage, arkClientTransport)
    {
        _logger = logger;
    }

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
            _logger?.LogDebug("Vtxo change handler cancelled");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(0, ex, "Error handling vtxo change event");
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
            _logger?.LogDebug("Contract change handler cancelled");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(0, ex, "Error handling contract change event");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Starting VTXO synchronization service");
        var multiToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        _queryTask = StartQueryLogic(multiToken.Token);
        await UpdateScriptsView(multiToken.Token);
    }

    private async Task UpdateScriptsView(CancellationToken token)
    {
        await _viewSyncLock.WaitAsync(token);
        try
        {
            var contracts = 
                await _contractStorage.LoadActiveContracts(null, token);
            
            var newViewOfScripts =
                contracts.Select(c => c.Script).ToHashSet();

            foreach (var vtxo in await _vtxoStorage.GetUnspentVtxos(token))
            {
                newViewOfScripts.Add(vtxo.Script);
            }

            if (newViewOfScripts.Count == 0)
                return;

            // We already have a stream with this exact script list
            if (newViewOfScripts.SetEquals(_lastViewOfScripts) && _streamTask is not null && !_streamTask.IsCompleted)
            {
                _logger?.LogDebug("Scripts view unchanged, skipping stream restart");
                return;
            }

            try
            {
                if (_restartCts is not null)
                    await _restartCts.CancelAsync();
                if (_streamTask is not null)
                    await _streamTask;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(0, ex, "Error cancelling previous stream during scripts view update");
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
        _logger?.LogDebug("Starting stream logic for {ScriptCount} scripts", scripts.Count);
        try
        {
            var restartableToken =
                CancellationTokenSource.CreateLinkedTokenSource(token, _shutdownCts.Token);
            await foreach (var vtxosToPoll in _arkClientTransport.GetVtxoToPollAsStream(scripts, restartableToken.Token))
            {
                await _readyToPoll.Writer.WriteAsync(vtxosToPoll, restartableToken.Token);
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            _logger?.LogWarning(0, ex, "Stream logic failed, restarting scripts view");
            await UpdateScriptsView(_shutdownCts.Token);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(0, ex, "Stream logic cancelled");
        }
    }

    private async Task? StartQueryLogic(CancellationToken cancellationToken)
    {
        await foreach (var pollBatch in _readyToPoll.Reader.ReadAllAsync(cancellationToken))
        {
            await foreach (var vtxo in _arkClientTransport.GetVtxoByScriptsAsSnapshot(pollBatch, cancellationToken))
            {
                // Upsert
                var updated = await _vtxoStorage.UpsertVtxo(vtxo, cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _logger?.LogDebug("Disposing VTXO synchronization service");
        await _shutdownCts.CancelAsync();

        try
        {
            if (_queryTask is not null)
                await _queryTask;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Query task cancelled during disposal");
        }
        try
        {
            if (_streamTask is not null)
                await _streamTask;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Stream task cancelled during disposal");
        }

        _logger?.LogInformation("VTXO synchronization service disposed");
    }
}