using NArk.Abstractions.Contracts;

namespace NArk.Tests.End2End.TestPersistance;

public class InMemoryContractStorage : IContractStorage
{
    private readonly Dictionary<string, HashSet<ArkContractEntity>> _contracts = new();

    public event EventHandler? ContractsChanged;

    public Task<IReadOnlySet<ArkContractEntity>> LoadAllContracts(string walletIdentifier,
        CancellationToken cancellationToken = default)
    {
        lock (_contracts)
        {
            return Task.FromResult<IReadOnlySet<ArkContractEntity>>(
                _contracts.TryGetValue(walletIdentifier, out var contracts) ? contracts : []);
        }
    }

    public Task<IReadOnlySet<ArkContractEntity>> LoadActiveContracts(IReadOnlyCollection<string> walletIdentifier,
        CancellationToken cancellationToken = default)
    {
        lock (_contracts)
            return Task.FromResult<IReadOnlySet<ArkContractEntity>>(_contracts
                .Where(x => walletIdentifier.Contains(x.Key))
                .SelectMany(x => x.Value)
                .Where(x => x.Important)
                .ToHashSet());
    }

    public Task<ArkContractEntity?> LoadContractByScript(string script, CancellationToken cancellationToken = default)
    {
        lock (_contracts)
        {
            return Task.FromResult(_contracts.Values.SelectMany(x => x).FirstOrDefault(x => x.Script == script));
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
            ContractsChanged?.Invoke(this, EventArgs.Empty);
        }

        return Task.CompletedTask;
    }
}