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
        var fingerprint = await addressProvider!.GetWalletFingerprint(walletIdentifier);
        
        if (htlc.Preimage is not null && OutputDescriptorHelpers.GetFingerprint(htlc.Receiver).Equals(fingerprint, StringComparison.InvariantCultureIgnoreCase))
        {
            return true;
        }

        if (htlc.RefundLocktime.IsTimeLock &&
            htlc.RefundLocktime.Date < DateTime.UtcNow && OutputDescriptorHelpers.GetFingerprint(htlc.Sender).Equals(fingerprint, StringComparison.InvariantCultureIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public async Task<ArkCoin> Transform(string walletIdentifier, ArkContract contract, ArkVtxo vtxo)
    {
        var htlc = contract as VHTLCContract;
        
        var addressProvider = await walletProvider.GetAddressProviderAsync(walletIdentifier);
        var fingerprint = await addressProvider!.GetWalletFingerprint(walletIdentifier);
        
        if (htlc!.Preimage is not null && OutputDescriptorHelpers.GetFingerprint(htlc.Receiver).Equals(fingerprint, StringComparison.InvariantCultureIgnoreCase))
        {
            return new ArkCoin(walletIdentifier, htlc, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight, vtxo.OutPoint, vtxo.TxOut, htlc.Receiver,
                htlc.CreateClaimScript(), new WitScript(Op.GetPushOp(htlc.Preimage!)), null, null, vtxo.Recoverable);
        }

        if (htlc.RefundLocktime.IsTimeLock &&
            htlc.RefundLocktime.Date < DateTime.UtcNow && OutputDescriptorHelpers.GetFingerprint(htlc.Sender).Equals(fingerprint, StringComparison.InvariantCultureIgnoreCase))
        {
            return new ArkCoin(walletIdentifier, htlc, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight, vtxo.OutPoint, vtxo.TxOut, htlc.Receiver,
                htlc.CreateRefundWithoutReceiverScript(), null, htlc.RefundLocktime, null, vtxo.Recoverable);
        }

        throw new InvalidOperationException("CanTransform should've return false for this coin");
    }
}