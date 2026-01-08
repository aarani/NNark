using NArk.Abstractions;

namespace NArk.Sweeper;

public interface ISweepPolicy
{
    public bool CanSweep(IEnumerable<ArkCoin> coins);
    public IAsyncEnumerable<ArkCoin> SweepAsync(IEnumerable<ArkCoin> coins);
}