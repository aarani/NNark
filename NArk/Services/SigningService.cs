using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Helpers;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Contracts;
using NArk.Helpers;
using NArk.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Services;

public class SigningService(
    IKeyStorage keyStorage,
    IContractStorage contractStorage,
    IClientTransport clientTransport,
    ILogger<SigningService>? logger = null
) : ISigningService
{
    public async Task<ArkCoin> GetVtxoCoinByContract(ArkContractEntity contractEntity, ArkVtxo vtxo,
        CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Getting PSBT signer for vtxo {TxId}:{Index}", vtxo.TransactionId, vtxo.TransactionOutputIndex);
        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        var parsedContract = ArkContractParser.Parse(contractEntity.Type, contractEntity.AdditionalData, serverInfo.Network);
        if (parsedContract is null)
        {
            logger?.LogWarning("Could not parse contract for vtxo {TxId}:{Index}", vtxo.TransactionId, vtxo.TransactionOutputIndex);
            throw new UnableToSignUnknownContracts("Could not parse contract");
        }
        return parsedContract.ToArkCoin(contractEntity.WalletIdentifier, vtxo);
    }

    public async Task<ArkCoin> GetCoin(ArkVtxo vtxo, CancellationToken cancellationToken = default)
    {
        logger?.LogDebug("Getting PSBT signer for vtxo by script {TxId}:{Index}", vtxo.TransactionId, vtxo.TransactionOutputIndex);
        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        var contracts = await contractStorage.LoadContractsByScripts([vtxo.Script], cancellationToken);
        if (contracts.SingleOrDefault() is not { } contract)
        {
            logger?.LogWarning("Could not find contract for vtxo {TxId}:{Index}", vtxo.TransactionId, vtxo.TransactionOutputIndex);
            throw new UnableToSignUnknownContracts("Could not find contract for vtxo");
        }
        var parsedContract = ArkContractParser.Parse(contract.Type, contract.AdditionalData, serverInfo.Network);
        if (parsedContract is null)
        {
            logger?.LogWarning("Could not parse contract for vtxo {TxId}:{Index}", vtxo.TransactionId, vtxo.TransactionOutputIndex);
            throw new UnableToSignUnknownContracts("Could not parse contract");
        }

        return parsedContract.ToArkCoin(contract.WalletIdentifier, vtxo);
    }

    public async Task<MusigPartialSignature> SignMusig(OutputDescriptor descriptor, MusigContext context, MusigPrivNonce nonce,
        CancellationToken cancellationToken = default)
    {
        var walletId = OutputDescriptorHelpers.Extract(descriptor).WalletId;
        var extKey = await keyStorage.GetPrivateKeyAsync(walletId, cancellationToken);
        var privateKey = await DerivePrivateKey(extKey, descriptor, cancellationToken);
        return context.Sign(privateKey, nonce);
    }

    public async Task<ECPrivKey> DerivePrivateKey(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        var extKey = await keyStorage.GetPrivateKeyAsync(OutputDescriptorHelpers.Extract(descriptor).WalletId, cancellationToken);
        return await DerivePrivateKey(extKey, descriptor, cancellationToken);
    }
    
    private Task<ECPrivKey> DerivePrivateKey(ExtKey extKey, OutputDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        var info = OutputDescriptorHelpers.Extract(descriptor);
        return Task.FromResult(ECPrivKey.Create(extKey.Derive(info.FullPath!).PrivateKey.ToBytes()));
    }
    
    public async Task SignAndFillPsbt(ArkCoin coin, PSBT psbt, TaprootReadyPrecomputedTransactionData precomputedTransactionData,
        TaprootSigHash sigHash = TaprootSigHash.Default, CancellationToken cancellationToken = default)
    {
        var psbtInput = coin.FillPsbtInput(psbt);

        if (psbtInput is null)
            return;

        var gtx = psbt.GetGlobalTransaction();
        var hash = gtx.GetSignatureHashTaproot(precomputedTransactionData,
            new TaprootExecutionData((int)psbtInput.Index, coin.SpendingScript.LeafHash)
            {
                SigHash = sigHash
            });

        var extKey = await keyStorage.GetPrivateKeyAsync(coin.WalletIdentifier, cancellationToken);
        var privateKey = await DerivePrivateKey(extKey, coin.SignerDescriptor, cancellationToken);
        
        var sig = privateKey.SignBIP340(hash.ToBytes());

        psbtInput.SetTaprootScriptSpendSignature(privateKey.CreateXOnlyPubKey(), coin.SpendingScript.LeafHash, sig);
    }
}