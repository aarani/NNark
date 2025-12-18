namespace NArk.Abstractions.Wallets;

public interface IWalletStorage
{
    Task<WalletData> LoadWallet(string walletIdentifier);
    Task SaveWallet(string walletId, WalletData walletData, string? walletFingerprint = null);
}