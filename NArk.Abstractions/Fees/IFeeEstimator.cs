using NBitcoin;

namespace NArk.Abstractions.Fees;

public interface IFeeEstimator
{
    public decimal EstimateFee(Coin[] coins, TxOut[] target);
}