using NArk.Abstractions.Contracts;
using NBitcoin.Scripting;

namespace NArk.Abstractions.Wallets;

public interface IArkadeAddressProvider
{
    Task<bool> IsOurs(OutputDescriptor descriptor, CancellationToken cancellationToken = default);
    Task<OutputDescriptor> GetNextSigningDescriptor(string identifier, CancellationToken cancellationToken = default);
    Task<ArkContract> GetNextPaymentContract(string identifier, CancellationToken cancellationToken = default);
}