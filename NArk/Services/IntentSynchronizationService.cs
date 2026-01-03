using System.Threading.Channels;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Enums;
using NArk.Events;
using NArk.Extensions;
using NArk.Transport;

namespace NArk.Services;

public class IntentSynchronizationService(
    IIntentStorage intentStorage,
    IClientTransport clientTransport,
    ISafetyService safetyService,
    IEnumerable<IEventHandler<PostIntentSubmissionEvent>> eventHandlers) : IAsyncDisposable
{

    public IntentSynchronizationService(IIntentStorage intentStorage,
        IClientTransport clientTransport,
        ISafetyService safetyService) : this(intentStorage, clientTransport, safetyService, [])
    {
        
    }
    
    private readonly CancellationTokenSource _shutdownCts = new();

    private readonly Channel<string> _submitTriggerChannel = Channel.CreateUnbounded<string>();
    private Task? _intentSubmitLoop;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        intentStorage.IntentChanged += OnIntentChanged;
        var multiToken = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, cancellationToken);
        _intentSubmitLoop = DoIntentSubmitLoop(multiToken.Token);
        _submitTriggerChannel.Writer.TryWrite("START");
        return Task.CompletedTask;
    }

    private void OnIntentChanged(object? sender, ArkIntent intent)
    {
        _submitTriggerChannel.Writer.TryWrite("INTENT_CHANGED");
    }

    private async Task DoIntentSubmitLoop(CancellationToken token)
    {
        try
        {
            await foreach (var _ in _submitTriggerChannel.Reader.ReadAllAsync(token))
            {
                token.ThrowIfCancellationRequested();

                var intentsToSubmit = await intentStorage.GetUnsubmittedIntents(DateTimeOffset.UtcNow, token);
                foreach (var intentToSubmit in intentsToSubmit)
                {
                    // In case storage did not respect our wish...
                    if (intentToSubmit.ValidFrom > DateTimeOffset.UtcNow ||
                            intentToSubmit.ValidUntil < DateTimeOffset.UtcNow)
                        continue;

                    await SubmitIntent(intentToSubmit, token);
                }
            }
        }
        catch (OperationCanceledException)
        {

        }
    }

    private async Task SubmitIntent(ArkIntent intentToSubmit, CancellationToken token)
    {
        await using var @lock = await safetyService.LockKeyAsync($"intent::{intentToSubmit.InternalId}", token);
        var intentAfterLock = await intentStorage.GetIntentByInternalId(intentToSubmit.InternalId, token);
        if (intentAfterLock is null)
            throw new Exception("Should not happen, intent disappeared from storage mid-action");

        try
        {
            try
            {
                var intentId =
                    await clientTransport.RegisterIntent(intentAfterLock, token);

                var now = DateTimeOffset.UtcNow;

                await intentStorage.SaveIntent(
                    intentAfterLock.WalletId,
                    intentAfterLock with
                    {
                        IntentId = intentId,
                        State = ArkIntentState.WaitingForBatch,
                        UpdatedAt = now
                    }, token);

                await eventHandlers.SafeHandleEventAsync(new PostIntentSubmissionEvent(intentAfterLock, now, true,
                    ActionState.Successful, null), token);
            }
            catch (AlreadyLockedVtxoException)
            {
                await clientTransport.DeleteIntent(intentAfterLock, token);

                var intentId =
                    await clientTransport.RegisterIntent(intentAfterLock, token);

                var now = DateTimeOffset.UtcNow;

                await intentStorage.SaveIntent(
                    intentAfterLock.WalletId,
                    intentAfterLock with
                    {
                        IntentId = intentId,
                        State = ArkIntentState.WaitingForBatch,
                        UpdatedAt = now
                    }, token);

                await eventHandlers.SafeHandleEventAsync(new PostIntentSubmissionEvent(intentAfterLock, now, false,
                    ActionState.Successful, null), token);
            }
        }
        catch (Exception ex)
        {
            var now = DateTimeOffset.UtcNow;
            
            await intentStorage.SaveIntent(
                intentAfterLock.WalletId,
                intentAfterLock with
                {
                    State = ArkIntentState.Cancelled,
                    UpdatedAt = now
                }, token);
            
            await eventHandlers.SafeHandleEventAsync(new PostIntentSubmissionEvent(intentAfterLock, now, false,
                ActionState.Failed, $"Intent submission failed with ex: {ex}"), token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        intentStorage.IntentChanged -= OnIntentChanged;

        await _shutdownCts.CancelAsync();

        try
        {
            if (_intentSubmitLoop is not null)
                await _intentSubmitLoop;
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
    }

    
}