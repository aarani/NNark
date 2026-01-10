using NArk.Abstractions.Scripts;

namespace NArk.Abstractions.Contracts;

public interface IContractStorage: IActiveScriptsProvider
{
    event EventHandler<ArkContractEntity>? ContractsChanged;
    Task<IReadOnlySet<ArkContractEntity>> LoadAllContractsByWallet(string walletIdentifier, CancellationToken cancellationToken = default);
    Task<IReadOnlySet<ArkContractEntity>> LoadActiveContracts(IReadOnlyCollection<string>? walletIdentifiers = null, CancellationToken cancellationToken = default);
    Task<IReadOnlySet<ArkContractEntity>> LoadContractsByScripts(string[] scripts, CancellationToken cancellationToken = default);
    Task SaveContract(string walletIdentifier, ArkContractEntity walletEntity, CancellationToken cancellationToken = default);

    async Task<HashSet<string>> IActiveScriptsProvider.GetActiveScripts(CancellationToken cancellationToken)
    {
        return (await LoadActiveContracts(null, cancellationToken)).Select(c => c.Script).ToHashSet();
    }
}