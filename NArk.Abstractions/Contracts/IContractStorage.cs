namespace NArk.Abstractions.Contracts;

public interface IContractStorage
{
    event EventHandler? ContractsChanged;
    Task<IReadOnlySet<ArkContractEntity>> LoadAllContracts(string walletIdentifier, CancellationToken cancellationToken = default);
    Task<IReadOnlySet<ArkContractEntity>> LoadActiveContracts(IReadOnlyCollection<string> walletIdentifier, CancellationToken cancellationToken = default);
    Task<ArkContractEntity?> LoadContractByScript(string script, CancellationToken cancellationToken = default);
    Task SaveContract(string walletIdentifier, ArkContractEntity walletEntity, CancellationToken cancellationToken = default);
}