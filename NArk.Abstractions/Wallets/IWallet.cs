using NBitcoin.Scripting;

namespace NArk.Abstractions.Wallets;

public interface IWallet
{
    Task CreateNewWallet(string walletIdentifier, CancellationToken cancellationToken = default);
    Task<string> GetWalletFingerprint(string walletIdentifier, CancellationToken cancellationToken = default);
    Task<ISigningEntity> GetNewSigningEntity(string walletIdentifier, CancellationToken cancellationToken = default);
    Task<ISigningEntity> FindSigningEntity(OutputDescriptor outputDescriptor, CancellationToken cancellationToken = default);
}