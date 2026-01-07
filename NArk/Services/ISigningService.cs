using NArk.Abstractions;
using NArk.Abstractions.Contracts;

using NArk.Abstractions.VTXOs;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Services;

public interface ISigningService
{
    Task<MusigPartialSignature> SignMusig(
        OutputDescriptor descriptor,
        MusigContext context,
        MusigPrivNonce nonce,
        CancellationToken cancellationToken = default);

    Task SignAndFillPsbt(ArkCoin coin,
        PSBT psbt,
        TaprootReadyPrecomputedTransactionData precomputedTransactionData,
        TaprootSigHash sigHash = TaprootSigHash.Default,
        CancellationToken cancellationToken = default);

    Task<ECPrivKey> DerivePrivateKey(OutputDescriptor descriptor, CancellationToken cancellationToken = default);

}