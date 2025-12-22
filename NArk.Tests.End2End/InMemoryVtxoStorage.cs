using NArk.Abstractions.VTXOs;
using NBitcoin;

namespace NArk.Tests.End2End;

public class InMemoryVtxoStorage: IVtxoStorage
{
    public async Task SaveVtxo(ArkVtxo vtxo)
    {
        throw new NotImplementedException();
    }

    public async Task<ArkVtxo> GetVtxoByOutPoint(OutPoint outpoint)
    {
        throw new NotImplementedException();
    }

    public async Task<IReadOnlyCollection<ArkVtxo>> GetVtxosByScripts(IReadOnlyCollection<string> scripts, bool allowSpent = false)
    {
        throw new NotImplementedException();
    }

    public async Task<IReadOnlyCollection<ArkVtxo>> GetUnspentVtxos()
    {
        throw new NotImplementedException();
    }

    public async Task<IReadOnlyCollection<ArkVtxo>> GetAllVtxos()
    {
        throw new NotImplementedException();
    }
}