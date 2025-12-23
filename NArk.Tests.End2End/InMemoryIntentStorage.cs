using NArk.Abstractions.Intents;
using NBitcoin;

namespace NArk.Tests.End2End;

public class InMemoryIntentStorage : IIntentStorage
{
    public event EventHandler? IntentChanged;
    private readonly Dictionary<string, HashSet<ArkIntent>> _intents = new();

    public Task SaveIntent(string walletIdentifier, ArkIntent intent)
    {
        lock (_intents)
        {
            if (_intents.TryGetValue(walletIdentifier, out var intents))
            {
                intents.Remove(intent);
                intents.Add(intent);
            }
            else
                _intents[walletIdentifier] = new HashSet<ArkIntent>(ArkIntent.InternalIdComparer) { intent };

            try
            {
                IntentChanged?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // ignored
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<ArkIntent>> GetIntents(string walletId)
    {
        lock (_intents)
        {
            return Task.FromResult<IReadOnlyCollection<ArkIntent>>(_intents[walletId]);
        }
    }

    public Task<ArkIntent?> GetIntentByInternalId(string walletId, Guid internalId)
    {
        lock (_intents)
        {
            return Task.FromResult(_intents[walletId].FirstOrDefault(intent => intent.InternalId == internalId));
        }
    }

    public Task<ArkIntent?> GetIntentByIntentId(string walletId, string intentId)
    {
        lock (_intents)
        {
            return Task.FromResult(_intents[walletId].FirstOrDefault(intent => intent.IntentId == intentId));
        }
    }

    public Task<IReadOnlyCollection<ArkIntent>> GetIntentsByInputs(string walletId, OutPoint[] inputs,
        bool pendingOnly = true)
    {
        lock (_intents)
        {
            return Task.FromResult<IReadOnlyCollection<ArkIntent>>(!_intents.TryGetValue(walletId, out var intents)
                ? []
                : intents.Where(intent => inputs.Intersect(intent.IntentVtxos).Any()).ToList());
        }
    }

    public Task<IReadOnlyCollection<ArkIntent>> GetUnsubmittedIntents()
    {
        return Task.FromResult<IReadOnlyCollection<ArkIntent>>(_intents.SelectMany(i =>
            i.Value.Where(intent => intent is { State: ArkIntentState.WaitingToSubmit, IntentId: null })).ToArray());
    }
}