using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Contracts;
using NBitcoin;

namespace NArk.Transformers;

public class HashLockedContractTransformer: IContractTransformer
{
    public async Task<bool> CanTransform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        return contract is HashLockedArkPaymentContract;
    }

    public async Task<ArkCoin> Transform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        var contractObj = contract as HashLockedArkPaymentContract;
        return new ArkCoin(walletIdentifier, contractObj!, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight, vtxo.OutPoint, vtxo.TxOut, contractObj!.User ?? throw new InvalidOperationException("User is required for claim script generation"),
            contractObj!.CreateClaimScript(), new WitScript(Op.GetPushOp(contractObj.Preimage)), null, null, vtxo.Recoverable);
    }
}