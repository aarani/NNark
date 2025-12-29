using NBitcoin;

namespace NArk.Abstractions.VTXOs;

public interface IVtxoStorage
{
    public event EventHandler<ArkVtxo>? VtxosChanged;

    Task SaveVtxo(ArkVtxo vtxo, CancellationToken cancellationToken = default);
    Task<ArkVtxo?> GetVtxoByOutPoint(OutPoint outpoint, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<ArkVtxo>> GetVtxosByScripts(IReadOnlyCollection<string> scripts, bool allowSpent = false
        , CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<ArkVtxo>> GetUnspentVtxos(CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<ArkVtxo>> GetAllVtxos(CancellationToken cancellationToken = default);
}