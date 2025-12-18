namespace NArk.Abstractions.Wallets;

public interface IWallet
{
    Task CreateNewWallet(string walletIdentifier);
    Task<ISigningEntity> GetSigningEntity(string walletIdentifier);
}