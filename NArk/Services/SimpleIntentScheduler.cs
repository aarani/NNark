using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Intents;

namespace NArk.Services;

public class SimpleIntentScheduler(IContractService contractService, IChainTimeProvider chainTimeProvider, TimeSpan? threshold, uint? thresholdHeight) : IIntentScheduler
{
    public async Task<IReadOnlyCollection<ArkIntentSpec>> GetIntentsToSubmit(
        IReadOnlyCollection<ArkCoinLite> unspentVtxos, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chainTimeProvider);
        ArgumentNullException.ThrowIfNull(threshold);
        ArgumentNullException.ThrowIfNull(thresholdHeight);

        if (unspentVtxos.Count == 0) return [];

        var chainTime = await chainTimeProvider.GetChainTime(cancellationToken);

        var coins = unspentVtxos
            .Where(v => v.Recoverable || (v.ExpiryAt is { } exp && exp + threshold.Value > chainTime.Timestamp) ||
                        (v.ExpiryAtHeight is { } height && height + thresholdHeight.Value > chainTime.Height))
            .GroupBy(v => v.WalletIdentifier);

        List<ArkIntentSpec> intentSpecs = [];

        foreach (var g in coins)
        {
            intentSpecs.Add(
                new ArkIntentSpec(
                g.ToArray(),
                [
                        new ArkTxOut(
                            ArkTxOutType.Vtxo,
                            g.Sum(coin => coin.Amount),
                            (await contractService.DerivePaymentContract(g.Key)).GetArkAddress()
                        )
                    ],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddHours(1)
                )
            );
        }

        return intentSpecs;

    }
}