using NBitcoin.Scripting;

namespace NArk.Abstractions.Wallets;

public interface IWallet
{
    Task CreateNewWallet(string walletIdentifier);
    Task<ISigningEntity> GetNewSigningEntity(string walletIdentifier);
    Task<ISigningEntity> FindSigningEntity(OutputDescriptor outputDescriptor);
}