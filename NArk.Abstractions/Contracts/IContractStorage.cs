namespace NArk.Abstractions.Contracts;

public interface IContractStorage
{
    event EventHandler? ContractsChanged;
    Task<IReadOnlySet<ArkContractEntity>> LoadAllContracts(string walletIdentifier);
    Task SaveContract(string walletIdentifier, ArkContractEntity walletEntity);
}