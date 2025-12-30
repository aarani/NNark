using NArk.Abstractions.Contracts;

namespace NArk.Tests.End2End.TestPersistance;

public class InMemoryContractStorage : IContractStorage
{
    private readonly Dictionary<string, HashSet<ArkContractEntity>> _contracts = new();

    public event EventHandler<ArkContractEntity>? ContractsChanged;

    public Task<IReadOnlySet<ArkContractEntity>> LoadAllContractsByWallet(string walletIdentifier,
        CancellationToken cancellationToken = default)
    {
        lock (_contracts)
        {
            return Task.FromResult<IReadOnlySet<ArkContractEntity>>(
                _contracts.TryGetValue(walletIdentifier, out var contracts) ? contracts : []);
        }
    }

    public Task<IReadOnlySet<ArkContractEntity>> LoadActiveContracts(IReadOnlyCollection<string>? walletIdentifiers = null,
        CancellationToken cancellationToken = default)
    {
        lock (_contracts)
            return Task.FromResult<IReadOnlySet<ArkContractEntity>>(_contracts
                .Where(x => walletIdentifiers is null || walletIdentifiers.Contains(x.Key))
                .SelectMany(x => x.Value)
                .Where(x => x.Important)
                .ToHashSet());
    }

    public Task<IReadOnlySet<ArkContractEntity>> LoadContractsByScripts(string[] scripts, CancellationToken cancellationToken = default)
    {
        lock (_contracts)
        {
            return Task.FromResult<IReadOnlySet<ArkContractEntity>>(_contracts.Values.SelectMany(x => x).Where(x => scripts.Contains(x.Script)).ToHashSet());
        }
    }

    public Task SaveContract(string walletIdentifier, ArkContractEntity contractEntity,
        CancellationToken cancellationToken = default)
    {
        lock (_contracts)
        {
            if (_contracts.TryGetValue(walletIdentifier, out var contracts))
                contracts.Add(contractEntity);
            else
                _contracts[walletIdentifier] = [contractEntity];
            ContractsChanged?.Invoke(this, contractEntity);
        }

        return Task.CompletedTask;
    }
}