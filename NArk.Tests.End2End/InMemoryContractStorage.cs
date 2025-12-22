using System.Collections.Concurrent;
using NArk.Abstractions.Contracts;

namespace NArk.Tests.End2End;

public class InMemoryContractStorage: IContractStorage
{
    private readonly Dictionary<string, HashSet<ArkContractEntity>> _contracts = new();
    
    public event EventHandler? ContractsChanged;
    public async Task<IReadOnlySet<ArkContractEntity>> LoadAllContracts(string walletIdentifier)
    {
        lock (_contracts)
            return _contracts[walletIdentifier];
    }

    public async Task<IReadOnlySet<ArkContractEntity>> LoadActiveContracts(IReadOnlyCollection<string> walletIdentifier)
    {
        lock (_contracts)
            return
                _contracts
                    .Where(x => walletIdentifier.Contains(x.Key))
                    .SelectMany(x => x.Value)
                    .Where(x => x.Important)
                    .ToHashSet();
    }

    public async Task<ArkContractEntity?> LoadContractByScript(string script)
    {
        throw new NotImplementedException();
    }

    public async Task SaveContract(string walletIdentifier, ArkContractEntity contractEntity)
    {
        lock (_contracts)
        {
            if (_contracts.TryGetValue(walletIdentifier, out var contracts))
                contracts.Add(contractEntity);
            else
                _contracts[walletIdentifier] = [contractEntity];
            ContractsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}