namespace NArk.Abstractions.VTXOs;

public interface IVtxoStorage
{
    Task SaveVtxo(ArkVtxo vtxo);
    Task<IEnumerable<ArkVtxo>> GetVtxosByScript(string script);
    Task<IEnumerable<ArkVtxo>> GetVtxosByWallet(string walletIdentifier);
    Task<IEnumerable<ArkVtxo>> GetUnspentVtxos();
    Task<IEnumerable<ArkVtxo>> GetAllVtxos();
}