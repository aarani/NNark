using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Transactions;

namespace NArk.Services;

public interface ISigningService
{
    Task<ArkPsbtSigner> GetPsbtSigner(ArkVtxo vtxo, CancellationToken cancellationToken = default);
    Task<ArkPsbtSigner> GetPsbtSigner(ArkCoin coin, CancellationToken cancellationToken = default);
    Task<ArkPsbtSigner> GetVtxoPsbtSignerByContract(ArkContractEntity contractEntity, ArkVtxo vtxo,
        CancellationToken cancellationToken = default);
}