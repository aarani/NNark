using NArk.Transactions;
using NBitcoin;

namespace NArk.CoinSelector;

public interface ICoinSelector
{
    IReadOnlyCollection<ArkPsbtSigner> SelectCoins(
        List<ArkPsbtSigner> availableCoins,
        Money targetAmount,
        Money dustThreshold,
        int currentSubDustOutputs);
}