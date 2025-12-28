using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Contracts;
using NArk.Transactions;
using NArk.Transport;
using NBitcoin;

namespace NArk.Services;

public class SigningService(
    IWallet wallet,
    IContractStorage contractStorage,
    IClientTransport clientTransport
) : ISigningService
{
    public async Task<ArkPsbtSigner> GetVtxoPsbtSignerByContract(ArkContractEntity contractEntity, ArkVtxo vtxo,
        CancellationToken cancellationToken = default)
    {
        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        var parsedContract = ArkContract.Parse(contractEntity.Type, contractEntity.AdditionalData, serverInfo.Network);
        if (parsedContract is null)
            throw new UnableToSignUnknownContracts("Could not parse contract");
        var arkCoin = parsedContract.ToArkCoin(contractEntity.WalletIdentifier, vtxo);
        return await GetPsbtSigner(arkCoin, cancellationToken);
    }

    public async Task<ArkPsbtSigner> GetPsbtSigner(ArkCoin coin, CancellationToken cancellationToken = default)
    {
        return new ArkPsbtSigner(coin, await wallet.FindSigningEntity(coin.SignerDescriptor, cancellationToken));
    }

    public async Task<ArkPsbtSigner> GetPsbtSigner(ArkVtxo vtxo, CancellationToken cancellationToken = default)
    {
        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        var contract = await contractStorage.LoadContractByScript(vtxo.Script, cancellationToken);
        if (contract is null)
            throw new UnableToSignUnknownContracts("Could not find contract for vtxo");
        var parsedContract = ArkContract.Parse(contract.Type, contract.AdditionalData, serverInfo.Network);
        if (parsedContract is null)
            throw new UnableToSignUnknownContracts("Could not parse contract");
        var arkCoin = parsedContract.ToArkCoin(contract.WalletIdentifier, vtxo);
        return await GetPsbtSigner(arkCoin, cancellationToken);
    }
}