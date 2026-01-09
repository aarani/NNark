using NBitcoin.Scripting;

namespace NArk.Abstractions.Wallets;

public interface IWallet
{
    Task CreateNewWallet(string walletIdentifier, CancellationToken cancellationToken = default);
    Task<string> GetWalletFingerprint(string walletIdentifier, CancellationToken cancellationToken = default);
    Task<OutputDescriptor> DeriveNextDescriptor(string walletIdentifier,
        CancellationToken cancellationToken = default);
}