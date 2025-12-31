using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NArk.Abstractions.Batches;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Batches;
using NArk.Extensions;
using NArk.Helpers;
using NArk.Models;
using NArk.Transactions;
using NArk.Transport;
using NBitcoin.Crypto;

namespace NArk.Services;

/// <summary>
/// Service for managing Ark intents with automatic submission, event monitoring, and batch participation
/// </summary>
public class BatchManagementService(
    IIntentStorage intentStorage,
    IWallet arkWalletService,
    IClientTransport clientTransport,
    IVtxoStorage vtxoStorage,
    IContractStorage contractStorage,
    ISigningService signingService,
    ISafetyService safetyService)
    : IAsyncDisposable
{
    private record BatchSessionWithConnectionId(
        int ConnectionId,
        BatchSession BatchSession
    );

    private record Connection(
        int Id,
        Task ConnectionTask,
        CancellationTokenSource CancellationTokenSource
    );

    // Polling intervals
    private static readonly TimeSpan EventStreamRetryDelay = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, ArkIntent> _activeIntents = new();
    private readonly ConcurrentDictionary<string, BatchSessionWithConnectionId> _activeBatchSessions = new();

    private readonly Dictionary<int, Connection> _connections = [];
    private readonly Dictionary<int, bool> _isReservedConnections = [];
    private readonly SemaphoreSlim _connectionManipulationSemaphore = new(1, 1);

    private readonly Channel<string> _triggerChannel = Channel.CreateUnbounded<string>();

    private CancellationTokenSource? _serviceCts;
    private bool _disposed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _serviceCts = new CancellationTokenSource();
        // Load existing WaitingForBatch intents and start a shared event stream
        await LoadActiveIntentsAsync(cancellationToken);
        _ = RunSharedEventStreamController(_serviceCts.Token);
        await _triggerChannel.Writer.WriteAsync("STARTUP", cancellationToken);
        intentStorage.IntentChanged += (_, _) => _triggerChannel.Writer.TryWrite("INTENT_CHANGED");
    }

    private async Task RunSharedEventStreamController(CancellationToken cancellationToken)
    {
        await foreach (var triggerReason in _triggerChannel.Reader.ReadAllAsync(cancellationToken))
        {
            await _connectionManipulationSemaphore.WaitAsync(cancellationToken);
            try
            {
                var unreservedConnections =
                    _isReservedConnections.Where(kvp => !kvp.Value).ToArray();

                if (unreservedConnections.Length > 1)
                    foreach (var (connId, _) in unreservedConnections.Skip(1))
                    {
                        if (!_connections.TryGetValue(connId, out var connection)) continue;

                        _ = connection.CancellationTokenSource.CancelAsync();
                        _connections.Remove(connId);
                        _isReservedConnections.Remove(connId);
                    }

                var connectionId = RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);
                var newCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                await LoadActiveIntentsAsync(cancellationToken);
                var newFreeConnection = RunSharedEventStreamAsync(connectionId, newCts.Token);
                _connections[connectionId] = new Connection(
                    connectionId,
                    newFreeConnection,
                    newCts
                );
                _isReservedConnections[connectionId] = false;

            }
            finally
            {
                _connectionManipulationSemaphore.Release();
            }
        }
    }


    #region Private Methods

    private async Task LoadActiveIntentsAsync(CancellationToken cancellationToken)
    {
        foreach (var intent in await intentStorage.GetActiveIntents(cancellationToken))
        {
            if (intent.IntentId is not null)
                _activeIntents[intent.IntentId] = intent;
        }
    }

    private async Task SaveToStorage(string intentId, Func<ArkIntent?, ArkIntent> updateFunc, CancellationToken cancellationToken = default)
    {
        var newValue = _activeIntents.AddOrUpdate(intentId, _ => updateFunc(null), (_, old) => updateFunc(old));
        await intentStorage.SaveIntent(newValue.WalletId, newValue, cancellationToken);
    }

    private async Task RunSharedEventStreamAsync(int connectionId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Build topics from all active intents (VTXOs + cosigner public keys)
                var vtxoTopics = _activeIntents.Values
                    .SelectMany(intent => intent.IntentVtxos
                        .Select(iv => $"{iv.Hash}:{iv.N}"));

                var cosignerTopics = _activeIntents.Values
                    .SelectMany(intent => ExtractCosignerKeys(intent.RegisterProofMessage));

                var topics =
                    vtxoTopics.Concat(cosignerTopics).ToHashSet();

                // If we have no topic to listen for, jump out.
                if (topics.Count is 0) return;

                await foreach (var eventResponse in clientTransport.GetEventStreamAsync(new GetEventStreamRequest(topics.ToArray()), cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await ProcessEventForAllIntentsAsync(connectionId, eventResponse, CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
                //logger.LogInformation("Stream was cancelled, possibly switching to a new stream...");
            }
            catch
            {
                //logger.LogError(ex, "Error in shared event stream, restarting in {Seconds} seconds", EventStreamRetryDelay.TotalSeconds);
                await Task.Delay(EventStreamRetryDelay, cancellationToken);
            }
            finally
            {
                _connections[connectionId].CancellationTokenSource.Dispose();
                _connections.Remove(connectionId);
                _isReservedConnections.Remove(connectionId);
            }
        }
    }

    private async Task ProcessEventForAllIntentsAsync(int connectionId, BatchEvent eventResponse, CancellationToken cancellationToken)
    {
        // Handle BatchStarted event first - check all intents at once
        if (eventResponse is BatchStartedEvent batchStartedEvent)
        {
            await HandleBatchStartedForAllIntentsAsync(connectionId, batchStartedEvent, cancellationToken);
        }

        // Process event for each active intent that might be affected
        foreach (var (intentId, intent) in _activeIntents.ToArray())
        {
            try
            {
                // If we have an active batch session, pass all events to it
                if (_activeBatchSessions.TryGetValue(intentId, out var batchSession))
                {
                    if (batchSession.ConnectionId != connectionId)
                    {
                        // This event is from a different connection, skip
                        continue;
                    }

                    var isComplete = await batchSession.BatchSession.ProcessEventAsync(eventResponse, cancellationToken);
                    if (isComplete)
                    {
                        _activeBatchSessions.TryRemove(intentId, out _);

                        await _connectionManipulationSemaphore.WaitAsync(_serviceCts!.Token);
                        try
                        {
                            _isReservedConnections[connectionId] = false;
                        }
                        finally
                        {
                            _connectionManipulationSemaphore.Release();
                        }

                        TriggerStreamUpdate();
                    }
                }

                // Handle events that affect this intent
                switch (eventResponse)
                {
                    case BatchFailedEvent batchFailedEvent:
                        if (batchFailedEvent.Id == intent.BatchId)
                        {
                            await HandleBatchFailedAsync(intent, batchFailedEvent, cancellationToken);
                            _activeBatchSessions.TryRemove(intentId, out _);
                            _activeIntents.TryRemove(intentId, out _);
                            TriggerStreamUpdate();
                        }
                        break;

                    case BatchFinalizedEvent batchFinalized:
                        if (batchFinalized.Id == intent.BatchId)
                        {
                            await HandleBatchFinalizedAsync(intent, batchFinalized, cancellationToken);
                            _activeBatchSessions.TryRemove(intentId, out _);
                            _activeIntents.TryRemove(intentId, out _);
                            TriggerStreamUpdate();
                        }
                        break;
                }
            }
            catch
            {
                //TODO: log
            }
        }
    }

    private void TriggerStreamUpdate()
    {
        _triggerChannel.Writer.TryWrite("STREAM_UPDATE_REQUESTED");
    }

    private async Task HandleBatchStartedForAllIntentsAsync(
        int connectionId,
        BatchStartedEvent batchEvent,
        CancellationToken cancellationToken)
    {
        // Build a map of intent ID hashes to IDs for efficient lookup
        var intentHashMap = new Dictionary<string, string>();
        foreach (var (intentId, _) in _activeIntents)
        {
            var intentIdBytes = Encoding.UTF8.GetBytes(intentId);
            var intentIdHash = Hashes.SHA256(intentIdBytes);
            var intentIdHashStr = Convert.ToHexStringLower(intentIdHash);
            intentHashMap[intentIdHashStr] = intentId;
        }

        // Find all our intents that are included in this batch
        var selectedIntentIds = new List<string>();
        foreach (var intentIdHash in batchEvent.IntentIdHashes)
        {
            if (intentHashMap.TryGetValue(intentIdHash, out var intentId))
            {
                selectedIntentIds.Add(intentId);
            }
        }

        if (selectedIntentIds.Count == 0)
        {
            return; // None of our intents in this batch
        }

        // Load all VTXOs and contracts for selected intents in one efficient query
        var walletIds = selectedIntentIds
            .Select(id => _activeIntents.TryGetValue(id, out var intent) ? intent.WalletId : null)
            .Where(wid => wid != null)
            .Select(wid => wid!)
            .Distinct()
            .ToArray();

        if (walletIds.Length == 0)
        {
            return;
        }

        // Collect all VTXO outpoints from all selected intents
        var allVtxoOutpoints = selectedIntentIds
            .Where(id => _activeIntents.ContainsKey(id))
            .SelectMany(id => _activeIntents[id].IntentVtxos)
            .ToHashSet();

        // Get spendable coins for all wallets, filtered by the specific VTXOs locked in intents

        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        // Confirm registration and create batch sessions for all selected intents
        foreach (var intentId in selectedIntentIds)
        {
            if (!_activeIntents.TryGetValue(intentId, out var intent))
                continue;

            try
            {
                // Get signer
                var signer = await arkWalletService.FindSigningEntity(KeyExtensions.ParseOutputDescriptor(intent.SignerDescriptor, serverInfo.Network));

                HashSet<ArkPsbtSigner> allWalletCoins = [];
                foreach (var outpoint in allVtxoOutpoints)
                {
                    allWalletCoins.Add(
                        await signingService.GetPsbtSigner(
                            await vtxoStorage.GetVtxoByOutPoint(outpoint, cancellationToken) ??
                            throw new InvalidOperationException("Unknown vtxo outpoint"), cancellationToken)
                    );
                }

                // Filter to only the VTXOs locked by this intent
                var intentVtxoOutpoints = intent.IntentVtxos.ToHashSet();

                var spendableCoins = allWalletCoins
                    .Where(coin => intentVtxoOutpoints.Contains(coin.Coin.Outpoint))
                    .ToList();

                if (spendableCoins.Count == 0)
                {
                    continue;
                }

                await _connectionManipulationSemaphore.WaitAsync(cancellationToken);

                try
                {
                    _isReservedConnections[connectionId] = true;

                    // Confirm registration
                    await clientTransport.ConfirmRegistrationAsync(
                        intentId,
                        cancellationToken: cancellationToken);
                }
                finally
                {
                    _connectionManipulationSemaphore.Release();
                }

                await SaveToStorage(intentId, arkIntent => (arkIntent ?? throw new InvalidOperationException("Failed to find intent in cache")) with
                {
                    BatchId = batchEvent.Id,
                    State = ArkIntentState.BatchInProgress,
                    UpdatedAt = DateTimeOffset.UtcNow
                }, cancellationToken);

                await LoadActiveIntentsAsync(cancellationToken);

                // Create and initialize a batch session
                var session = new BatchSession(
                    clientTransport,
                    new TransactionHelpers.ArkTransactionBuilder(clientTransport, safetyService, intentStorage),
                    serverInfo.Network,
                    signer,
                    intent,
                    spendableCoins.ToArray(),
                    batchEvent);

                await session.InitializeAsync(cancellationToken);

                // Store the session so events can be passed to it

                await _connectionManipulationSemaphore.WaitAsync(_serviceCts!.Token);
                try
                {
                    _activeBatchSessions[intent.IntentId!] = new BatchSessionWithConnectionId(
                        connectionId,
                        session
                    );
                }
                finally
                {
                    _connectionManipulationSemaphore.Release();
                }

                _ = RunSharedEventStreamController(_serviceCts!.Token);

            }
            catch
            {
                // ignored
            }
        }
    }

    private static IEnumerable<string> ExtractCosignerKeys(string registerProofMessage)
    {
        try
        {
            var message = JsonSerializer.Deserialize<Messages.RegisterIntentMessage>(registerProofMessage);
            return message?.CosignersPublicKeys ?? [];
        }
        catch (Exception)
        {
            // If we can't parse the message, return empty
            return [];
        }
    }

    private async Task HandleBatchFailedAsync(
        ArkIntent intent,
        BatchFailedEvent batchEvent,
        CancellationToken cancellationToken)
    {
        if (intent.BatchId == batchEvent.Id)
        {
            await SaveToStorage(intent.IntentId!, arkIntent => (arkIntent ?? throw new InvalidOperationException("Failed to find intent in cache")) with
            {
                State = ArkIntentState.BatchFailed,
                CancellationReason = $"Batch failed: {batchEvent.Reason}",
                UpdatedAt = DateTimeOffset.UtcNow
            }, cancellationToken);
        }
    }

    private async Task HandleBatchFinalizedAsync(
        ArkIntent intent,
        BatchFinalizedEvent finalizedEvent,
        CancellationToken cancellationToken)
    {
        await SaveToStorage(intent.IntentId!, arkIntent => (arkIntent ?? throw new InvalidOperationException("Failed to find intent in cache")) with
        {
            State = ArkIntentState.BatchSucceeded,
            CommitmentTransactionId = finalizedEvent.CommitmentTxId,
            UpdatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    #endregion


    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        try
        {
            if (_serviceCts is not null)
                await _serviceCts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, ignore
        }

        await _connectionManipulationSemaphore.WaitAsync();
        try
        {
            foreach (var (_, connection) in _connections)
            {
                try
                {
                    await connection.CancellationTokenSource.CancelAsync();
                }
                catch (ObjectDisposedException)
                {
                    // ignored
                }

                try
                {
                    await connection.ConnectionTask;
                }
                catch
                {
                    // ignored
                }

                try
                {
                    connection.CancellationTokenSource.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // ignored
                }
            }
        }
        finally
        {
            _connectionManipulationSemaphore.Release();
        }


        try
        {
            _connectionManipulationSemaphore.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, ignore
        }

        _serviceCts?.Dispose();

        _activeIntents.Clear();
        _activeBatchSessions.Clear();

        _disposed = true;
    }
}