using NArk.Scripts;

namespace NArk.Sweeper;

public interface ISweepPolicy
{
    public bool CanSweep(IEnumerable<ArkUnspendableCoin> coins);
    public IAsyncEnumerable<ArkCoin> SweepAsync(IEnumerable<ArkUnspendableCoin> coins);
}