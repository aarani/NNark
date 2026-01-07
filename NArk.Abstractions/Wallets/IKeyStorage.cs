using NBitcoin;

namespace NArk.Abstractions.Wallets;

public interface IKeyStorage
{
    Task AddPrivateKeyAsync(string walletIdentifier, ExtKey extKey, CancellationToken cancellationToken = default);
    Task<ExtKey> GetPrivateKeyAsync(string walletIdentifier, CancellationToken cancellationToken = default);
}