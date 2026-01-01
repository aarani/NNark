using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Contracts;
using NArk.Events;
using NArk.Extensions;
using NArk.Transport;

namespace NArk.Services;

public class ContractService(
    IWallet wallet,
    IContractStorage contractStorage,
    IClientTransport transport,
    IEnumerable<IEventHandler<NewContractActionEvent>> eventHandlers) : IContractService
{
    public ContractService(IWallet wallet,
        IContractStorage contractStorage,
        IClientTransport transport) : this(wallet, contractStorage, transport, [])
    {
    }
    
    public async Task<ArkContract> DerivePaymentContract(string walletId, CancellationToken cancellationToken = default)
    {
        var info = await transport.GetServerInfoAsync(cancellationToken);
        var signingEntity = await wallet.GetNewSigningEntity(walletId, cancellationToken);
        var contract = new ArkPaymentContract(
            info.SignerKey,
            info.UnilateralExit,
            await signingEntity.GetOutputDescriptor(cancellationToken)
        );
        await contractStorage.SaveContract(walletId, contract.ToEntity(walletId), cancellationToken);
        await eventHandlers.SafeHandleEventAsync(new NewContractActionEvent(contract, walletId), cancellationToken);
        return contract;
    }

    public async Task ImportContract(string walletId, ArkContract contract, CancellationToken cancellationToken = default)
    {
        var info = await transport.GetServerInfoAsync(cancellationToken);
        if (contract.Server is not null && !contract.Server.Equals(info.SignerKey))
            throw new InvalidOperationException("Cannot import contract with different server key");
        await contractStorage.SaveContract(walletId, contract.ToEntity(walletId), cancellationToken);
        await eventHandlers.SafeHandleEventAsync(new NewContractActionEvent(contract, walletId), cancellationToken);
    }

}