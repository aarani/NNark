namespace NArk.Abstractions.Wallets;

public interface IWalletStorage
{
    Task<IReadOnlySet<ArkWallet>> LoadAllWallets();
    Task<ArkWallet> LoadWallet(string walletIdentifierOrFingerprint);
    Task SaveWallet(string walletId, ArkWallet arkWallet, string? walletFingerprint = null);
}