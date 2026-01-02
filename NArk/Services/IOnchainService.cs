using NArk.Abstractions;
using NArk.Transactions;

namespace NArk.Services;

public interface IOnchainService
{
    Task<Guid> InitiateCollaborativeExit(ArkCoin[] inputs, ArkTxOut[] outputs, CancellationToken cancellationToken = default);
    Task<Guid> InitiateCollaborativeExit(ArkPsbtSigner[] inputs, ArkTxOut[] outputs, CancellationToken cancellationToken = default);
}