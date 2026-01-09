using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Contracts;
using NArk.Events;
using NArk.Extensions;
using NArk.Transport;

namespace NArk.Services;

public class ContractService(
    IWalletProvider walletProvider,
    IContractStorage contractStorage,
    IClientTransport transport,
    IEnumerable<IEventHandler<NewContractActionEvent>> eventHandlers,
    ILogger<ContractService>? logger = null) : IContractService
{
    public ContractService(IWalletProvider walletProvider,
        IContractStorage contractStorage,
        IClientTransport transport) : this(walletProvider, contractStorage, transport, [], null)
    {
    }

    public ContractService(IWalletProvider walletProvider,
        IContractStorage contractStorage,
        IClientTransport transport,
        ILogger<ContractService> logger) : this(walletProvider, contractStorage, transport, [], logger)
    {
    }

    public async Task<ArkContract> DerivePaymentContract(string walletId, CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Deriving payment contract for wallet {WalletId}", walletId);
      
        var addressProvider = await walletProvider.GetAddressProviderAsync(walletId, cancellationToken);
        
        var contract = await addressProvider!.GetNextPaymentContract(walletId, cancellationToken);
     
        await contractStorage.SaveContract(walletId, contract.ToEntity(walletId), cancellationToken);
        await eventHandlers.SafeHandleEventAsync(new NewContractActionEvent(contract, walletId), cancellationToken);
        logger?.LogInformation("Derived payment contract for wallet {WalletId}", walletId);
        return contract;
    }

    public async Task ImportContract(string walletId, ArkContract contract, CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Importing contract for wallet {WalletId}", walletId);
        var info = await transport.GetServerInfoAsync(cancellationToken);
        if (contract.Server is not null && !contract.Server.Equals(info.SignerKey))
        {
            logger?.LogWarning("Cannot import contract for wallet {WalletId}: server key mismatch", walletId);
            throw new InvalidOperationException("Cannot import contract with different server key");
        }
        await contractStorage.SaveContract(walletId, contract.ToEntity(walletId), cancellationToken);
        await eventHandlers.SafeHandleEventAsync(new NewContractActionEvent(contract, walletId), cancellationToken);
        logger?.LogInformation("Imported contract for wallet {WalletId}", walletId);
    }

}