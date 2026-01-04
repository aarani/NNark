using Microsoft.Extensions.Logging;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Contracts;
using NArk.Transactions;
using NArk.Transport;

namespace NArk.Services;

public class SigningService(
    IWallet wallet,
    IContractStorage contractStorage,
    IClientTransport clientTransport,
    ILogger<SigningService>? logger = null
) : ISigningService
{
    public async Task<ArkPsbtSigner> GetVtxoPsbtSignerByContract(ArkContractEntity contractEntity, ArkVtxo vtxo,
        CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Getting PSBT signer for vtxo {TxId}:{Index}", vtxo.TransactionId, vtxo.TransactionOutputIndex);
        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        var parsedContract = ArkContract.Parse(contractEntity.Type, contractEntity.AdditionalData, serverInfo.Network);
        if (parsedContract is null)
        {
            logger?.LogWarning("Could not parse contract for vtxo {TxId}:{Index}", vtxo.TransactionId, vtxo.TransactionOutputIndex);
            throw new UnableToSignUnknownContracts("Could not parse contract");
        }
        var arkCoin = parsedContract.ToArkCoin(contractEntity.WalletIdentifier, vtxo);
        return await GetPsbtSigner(arkCoin, cancellationToken);
    }

    public async Task<ArkPsbtSigner> GetPsbtSigner(ArkCoin coin, CancellationToken cancellationToken = default)
    {
        return new ArkPsbtSigner(coin, await wallet.FindSigningEntity(coin.SignerDescriptor, cancellationToken));
    }

    public async Task<ArkPsbtSigner> GetPsbtSigner(ArkVtxo vtxo, CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Getting PSBT signer for vtxo by script {TxId}:{Index}", vtxo.TransactionId, vtxo.TransactionOutputIndex);
        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        var contracts = await contractStorage.LoadContractsByScripts([vtxo.Script], cancellationToken);
        if (contracts.SingleOrDefault() is not { } contract)
        {
            logger?.LogWarning("Could not find contract for vtxo {TxId}:{Index}", vtxo.TransactionId, vtxo.TransactionOutputIndex);
            throw new UnableToSignUnknownContracts("Could not find contract for vtxo");
        }
        var parsedContract = ArkContract.Parse(contract.Type, contract.AdditionalData, serverInfo.Network);
        if (parsedContract is null)
        {
            logger?.LogWarning("Could not parse contract for vtxo {TxId}:{Index}", vtxo.TransactionId, vtxo.TransactionOutputIndex);
            throw new UnableToSignUnknownContracts("Could not parse contract");
        }
        var arkCoin = parsedContract.ToArkCoin(contract.WalletIdentifier, vtxo);
        return await GetPsbtSigner(arkCoin, cancellationToken);
    }
}