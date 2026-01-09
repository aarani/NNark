using NBitcoin.Scripting;

namespace NArk.Abstractions.Wallets;

public interface IArkadeAddressProvider
{
    Task<string> GetWalletFingerprint(CancellationToken cancellationToken = default);
    Task<OutputDescriptor> GetNewSigningDescriptor(string identifier,CancellationToken cancellationToken = default);
}