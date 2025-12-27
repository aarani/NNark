using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Intents;
using NArk.Models.Options;

namespace NArk.Services;

public class SimpleIntentScheduler(IContractService contractService, IChainTimeProvider chainTimeProvider, IOptions<SimpleIntentSchedulerOptions> options) : IIntentScheduler
{
    public async Task<IReadOnlyCollection<ArkIntentSpec>> GetIntentsToSubmit(
        IReadOnlyCollection<ArkCoinLite> unspentVtxos, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chainTimeProvider);
        if (options.Value.ThresholdHeight is null && options.Value.Threshold is null)
            throw new ArgumentNullException("Either thresholdHeight or threshold is required");

        if (unspentVtxos.Count == 0) return [];

        var chainTime = await chainTimeProvider.GetChainTime(cancellationToken);

        var coins = unspentVtxos
            .Where(v => v.Recoverable || (v.ExpiryAt is { } exp && exp + options.Value.Threshold > chainTime.Timestamp) ||
                        (v.ExpiryAtHeight is { } height && height + options.Value.ThresholdHeight > chainTime.Height))
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
                            (await contractService.DerivePaymentContract(g.Key, cancellationToken)).GetArkAddress()
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