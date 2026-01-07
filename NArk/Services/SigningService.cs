using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Helpers;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Contracts;
using NArk.Helpers;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Services;

public class SigningService(
    IKeyStorage keyStorage,
    ILogger<SigningService>? logger = null
) : ISigningService
{
    public async Task<MusigPartialSignature> SignMusig(OutputDescriptor descriptor, MusigContext context, MusigPrivNonce nonce,
        CancellationToken cancellationToken = default)
    {
        var walletId = OutputDescriptorHelpers.Extract(descriptor).WalletId;
        var extKey = await keyStorage.GetPrivateKeyAsync(walletId, cancellationToken);
        var privateKey = await DerivePrivateKey(extKey, descriptor, cancellationToken);
        return context.Sign(privateKey, nonce);
    }

    public async Task<ECPrivKey> DerivePrivateKey(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        var extKey = await keyStorage.GetPrivateKeyAsync(OutputDescriptorHelpers.Extract(descriptor).WalletId, cancellationToken);
        return await DerivePrivateKey(extKey, descriptor, cancellationToken);
    }

    private Task<ECPrivKey> DerivePrivateKey(ExtKey extKey, OutputDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        var info = OutputDescriptorHelpers.Extract(descriptor);
        return Task.FromResult(ECPrivKey.Create(extKey.Derive(info.FullPath!).PrivateKey.ToBytes()));
    }

    public async Task SignAndFillPsbt(ArkCoin coin, PSBT psbt, TaprootReadyPrecomputedTransactionData precomputedTransactionData,
        TaprootSigHash sigHash = TaprootSigHash.Default, CancellationToken cancellationToken = default)
    {
        var psbtInput = coin.FillPsbtInput(psbt);

        if (psbtInput is null)
            return;

        var gtx = psbt.GetGlobalTransaction();
        var hash = gtx.GetSignatureHashTaproot(precomputedTransactionData,
            new TaprootExecutionData((int)psbtInput.Index, coin.SpendingScript.LeafHash)
            {
                SigHash = sigHash
            });

        var extKey = await keyStorage.GetPrivateKeyAsync(coin.WalletIdentifier, cancellationToken);
        var privateKey = await DerivePrivateKey(extKey, coin.SignerDescriptor, cancellationToken);

        var sig = privateKey.SignBIP340(hash.ToBytes());

        psbtInput.SetTaprootScriptSpendSignature(privateKey.CreateXOnlyPubKey(), coin.SpendingScript.LeafHash, sig);
    }
}