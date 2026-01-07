using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Contracts;

namespace NArk.Transformers;

public class PaymentContractTransformer : IContractTransformer
{
    public async Task<bool> CanTransform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        return contract is ArkPaymentContract;
    }

    public async Task<ArkCoin> Transform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        var paymentContract = (contract as ArkPaymentContract)!;
        return new ArkCoin(walletIdentifier, contract, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight, vtxo.OutPoint, vtxo.TxOut, paymentContract.User ?? throw new InvalidOperationException("User is required for claim script generation"),
            paymentContract.CollaborativePath(), null, null, null, vtxo.Recoverable);
    }
}