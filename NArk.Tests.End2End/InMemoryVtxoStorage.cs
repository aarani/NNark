using System.Collections.Concurrent;
using NArk.Abstractions.VTXOs;
using NBitcoin;

namespace NArk.Tests.End2End;

public class InMemoryVtxoStorage : IVtxoStorage
{
    private ConcurrentDictionary<string, ArkVtxo> _vtxos = new();

    public virtual Task SaveVtxo(ArkVtxo vtxo, CancellationToken cancellationToken = default)
    {
        _vtxos[vtxo.OutPoint.ToString()] = vtxo;
        return Task.CompletedTask;
    }

    public Task<ArkVtxo?> GetVtxoByOutPoint(OutPoint outpoint, CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult<ArkVtxo?>(_vtxos[outpoint.ToString()]);
        }
        catch (KeyNotFoundException)
        {
            return Task.FromResult<ArkVtxo?>(null!);
        }
    }

    public Task<IReadOnlyCollection<ArkVtxo>> GetVtxosByScripts(IReadOnlyCollection<string> scripts,
        bool allowSpent = false,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyCollection<ArkVtxo>>(_vtxos.Values.Where(v => scripts.Contains(v.Script))
            .ToList());
    }

    public Task<IReadOnlyCollection<ArkVtxo>> GetUnspentVtxos(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyCollection<ArkVtxo>>(_vtxos.Values.Where(v => !v.IsSpent()).ToList());
    }

    public Task<IReadOnlyCollection<ArkVtxo>> GetAllVtxos(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyCollection<ArkVtxo>>(_vtxos.Values.ToList());
    }
}