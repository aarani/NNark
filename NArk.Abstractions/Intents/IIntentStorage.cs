namespace NArk.Abstractions.Intents;

public interface IIntentStorage
{
    public event EventHandler? IntentChanged;
    
    public Task SaveIntent(string walletId, ArkIntent intent);
    public Task<IEnumerable<ArkIntent>> GetIntents(string walletId);
    public Task<ArkIntent?> GetIntentByInternalId(string walletId, int internalId);
    public Task<ArkIntent?> GetIntentByIntentId(string walletId, string intentId);
    public Task<IEnumerable<ArkIntent>> GetUnsubmittedIntents();
}