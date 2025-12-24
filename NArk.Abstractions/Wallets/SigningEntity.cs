using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Abstractions.Wallets;

public interface ISigningEntity
{
    Task<Dictionary<string, string>> GetMetadata(CancellationToken cancellationToken = default);
    Task<string> GetFingerprint(CancellationToken cancellationToken = default);
    Task<OutputDescriptor> GetOutputDescriptor(CancellationToken cancellationToken = default);
    Task<ECPubKey> GetPublicKey(CancellationToken cancellationToken = default);
    Task<ECPrivKey> DerivePrivateKey(CancellationToken cancellationToken = default);
    Task<SignResult> SignData(uint256 hash, CancellationToken cancellationToken = default);
    Task<MusigPartialSignature> SignMusig(MusigContext context,
        MusigPrivNonce nonce,
        CancellationToken cancellationToken = default);
}