namespace NArk.Abstractions.Wallets;

public interface IWalletStorage
{
    Task<IReadOnlySet<ArkWallet>> LoadAllWallets(CancellationToken cancellationToken = default);
    Task<ArkWallet> LoadWallet(string walletIdentifierOrFingerprint, CancellationToken cancellationToken = default);
    Task SaveWallet(string walletId, ArkWallet arkWallet, CancellationToken cancellationToken = default);
}