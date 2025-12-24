using NArk.Contracts;

namespace NArk.Services;

public interface IContractService
{
    Task<ArkContract> DerivePaymentContract(string walletId, CancellationToken cancellationToken = default);
    Task ImportContract(string walletId, ArkContract contract, CancellationToken cancellationToken = default);
}