using System.Threading.Channels;
using NArk.Abstractions.Intents;
using NArk.Transport;

namespace NArk.Services;

public class IntentSynchronizationService(
    IIntentStorage intentStorage,
    IClientTransport clientTransport
) : IAsyncDisposable
{
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

    private async Task<string> SubmitIntent(ArkIntent intentToSubmit, CancellationToken token)
    {
        try
        {
            var intentId =
                await clientTransport.RegisterIntent(intentToSubmit, token);

            await intentStorage.SaveIntent(
                intentToSubmit.WalletId,
                intentToSubmit with
                {
                    IntentId = intentId,
                    State = ArkIntentState.WaitingForBatch,
                    UpdatedAt = DateTimeOffset.UtcNow
                }, token);

            return intentId;
        }
        catch (AlreadyLockedVtxoException)
        {
            await clientTransport.DeleteIntent(intentToSubmit, token);

            var intentId =
                await clientTransport.RegisterIntent(intentToSubmit, token);

            await intentStorage.SaveIntent(
                intentToSubmit.WalletId,
                intentToSubmit with
                {
                    IntentId = intentId,
                    State = ArkIntentState.WaitingForBatch,
                    UpdatedAt = DateTimeOffset.UtcNow
                }, token);

            return intentId;
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