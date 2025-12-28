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
        intentStorage.IntentChanged += (_, _) => _submitTriggerChannel.Writer.TryWrite("INTENT_CHANGED");
        var multiToken = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, cancellationToken);
        _intentSubmitLoop = DoIntentSubmitLoop(multiToken.Token);
        _submitTriggerChannel.Writer.TryWrite("START");
        return Task.CompletedTask;
    }

    private async Task DoIntentSubmitLoop(CancellationToken token)
    {
        await foreach (var _ in _submitTriggerChannel.Reader.ReadAllAsync(token))
        {
            var intentsToSubmit = await intentStorage.GetUnsubmittedIntents(token);
            foreach (var intentToSubmit in intentsToSubmit)
            {
                await SubmitIntent(intentToSubmit);
            }
        }
    }

    private async Task<string> SubmitIntent(ArkIntent intentToSubmit)
    {
        try
        {
            var intentId =
                await clientTransport.RegisterIntent(intentToSubmit, _shutdownCts.Token);

            await intentStorage.SaveIntent(
                intentToSubmit.WalletId,
                intentToSubmit with
                {
                    IntentId = intentId,
                    State = ArkIntentState.WaitingForBatch,
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            );

            return intentId;
        }
        catch (AlreadyLockedVtxoException)
        {
            await clientTransport.DeleteIntent(intentToSubmit, _shutdownCts.Token);

            var intentId =
                await clientTransport.RegisterIntent(intentToSubmit, _shutdownCts.Token);

            await intentStorage.SaveIntent(
                intentToSubmit.WalletId,
                intentToSubmit with
                {
                    IntentId = intentId,
                    State = ArkIntentState.WaitingForBatch,
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            );

            return intentId;
        }
    }

    public async ValueTask DisposeAsync()
    {
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