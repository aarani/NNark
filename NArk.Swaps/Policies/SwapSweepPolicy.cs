using NArk.Abstractions;
using NArk.Contracts;
using NArk.Sweeper;

namespace NArk.Swaps.Policies;

public class SwapSweepPolicy : ISweepPolicy
{
    // Lets use this as IsPolicyEnabled for now...
    public bool CanSweep(IEnumerable<ArkCoin> coins) =>
        coins.Any(c => c.Contract is VHTLCContract);
    public async IAsyncEnumerable<ArkCoin> SweepAsync(IEnumerable<ArkCoin> coins)
    {
        coins = coins.Where(c => c.Contract is VHTLCContract);
        foreach (var coin in coins)
        {
            if (coin.Contract is not VHTLCContract htlc) continue;

            //FIXME: what to put here? :/
            yield return coin;
        }
    }
}