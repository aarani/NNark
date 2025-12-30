using NArk.Abstractions;
using NArk.Transactions;
using NBitcoin;

namespace NArk.Services;

public interface ISpendingService
{
    Task<uint256> Spend(string walletId, ArkPsbtSigner[] inputs, ArkTxOut[] outputs, CancellationToken cancellationToken = default);
    Task<uint256> Spend(string walletId, ArkTxOut[] outputs, CancellationToken cancellationToken = default);
}