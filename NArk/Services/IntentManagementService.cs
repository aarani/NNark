using System.Threading.Channels;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Transport;

namespace NArk.Services;

public class IntentManagementService(
    IWallet wallet,
    IIntentStorage intentStorage,
    IVtxoStorage vtxoStorage,
    IClientTransport clientTransport,
    IFeeEstimator feeEstimator /*, IBatchSelector? batchSelector = null*/) : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdownCts = new();
    
    private readonly Channel<string> _submitTriggerChannel = Channel.CreateUnbounded<string>();
    private Task? _intentSubmitLoop;
    
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        intentStorage.IntentChanged += (_, _) => _submitTriggerChannel.Writer.TryWrite("INTENT_CHANGED");
        var multiToken = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, cancellationToken);
        _intentSubmitLoop = DoIntentSubmitLoop(multiToken.Token);
        return Task.CompletedTask;
    }

    private async Task DoIntentSubmitLoop(CancellationToken token)
    {
        await foreach (var _ in _submitTriggerChannel.Reader.ReadAllAsync(token))
        {
            var intentsToSubmit = await intentStorage.GetUnsubmittedIntents();
            foreach (var intentToSubmit in intentsToSubmit)
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
            }
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