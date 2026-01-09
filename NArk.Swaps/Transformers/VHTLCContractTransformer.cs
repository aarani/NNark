using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Contracts;
using NArk.Swaps.Helpers;
using NArk.Transformers;
using NBitcoin;

namespace NArk.Swaps.Transformers;

public class VHTLCContractTransformer(IWalletProvider walletProvider): IContractTransformer
{
    public async Task<bool> CanTransform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        if (contract is not VHTLCContract htlc) return false;

        var addressProvider = await walletProvider.GetAddressProviderAsync(walletIdentifier);
        
        if (htlc.Preimage is not null && await addressProvider!.IsOurs(htlc.Receiver))
        {
            return true;
        }

        if (htlc.RefundLocktime.IsTimeLock &&
            htlc.RefundLocktime.Date < DateTime.UtcNow && await addressProvider!.IsOurs(htlc.Sender))
        {
            return true;
        }

        return false;
    }

    public async Task<ArkCoin> Transform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        var htlc = contract as VHTLCContract;
        
        var addressProvider = await walletProvider.GetAddressProviderAsync(walletIdentifier);
        
        if (htlc!.Preimage is not null &&  await addressProvider!.IsOurs(htlc.Receiver))
        {
            return new ArkCoin(walletIdentifier, htlc, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight, vtxo.OutPoint, vtxo.TxOut, htlc.Receiver,
                htlc.CreateClaimScript(), new WitScript(Op.GetPushOp(htlc.Preimage!)), null, null, vtxo.Recoverable);
        }

        if (htlc.RefundLocktime.IsTimeLock &&
            htlc.RefundLocktime.Date < DateTime.UtcNow && await addressProvider!.IsOurs(htlc.Sender))
        {
            return new ArkCoin(walletIdentifier, htlc, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight, vtxo.OutPoint, vtxo.TxOut, htlc.Receiver,
                htlc.CreateRefundWithoutReceiverScript(), null, htlc.RefundLocktime, null, vtxo.Recoverable);
        }

        throw new InvalidOperationException("CanTransform should've return false for this coin");
    }
}