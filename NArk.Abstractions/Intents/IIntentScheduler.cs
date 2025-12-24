namespace NArk.Abstractions.Intents;

public interface IIntentScheduler
{
    Task<IReadOnlyCollection<ArkIntentSpec>> GetIntentsToSubmit(IReadOnlyCollection<ArkCoinLite> unspentVtxos,
        CancellationToken cancellationToken = default);
}