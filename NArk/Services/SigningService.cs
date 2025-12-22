using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Contracts;
using NArk.Transactions;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Services;

public class SigningService(
    IWallet wallet,
    IContractStorage contractStorage,
    Network network
): ISigningService
{
    public async Task<ArkPsbtSigner> GetVtxoPsbtSignerByContract(ArkContractEntity contractEntity, ArkVtxo vtxo)
    {
        var parsedContract = ArkContract.Parse(contractEntity.Type, contractEntity.AdditionalData, network);
        if (parsedContract is null)
            throw new UnableToSignUnknownContracts("Could not parse contract");
        var arkCoin = parsedContract.ToArkCoin(contractEntity.WalletIdentifier, vtxo);
        return await GetPsbtSigner(arkCoin);
    }

    public async Task<ArkPsbtSigner> GetPsbtSigner(ArkCoin coin)
    {
        return new ArkPsbtSigner(coin, await wallet.FindSigningEntity(coin.SignerDescriptor));
    }
    
    
    public async Task<ArkPsbtSigner> GetPsbtSigner(ArkVtxo vtxo)
    {
        var contract = await contractStorage.LoadContractByScript(vtxo.Script);
        if (contract is null)
            throw new UnableToSignUnknownContracts("Could not find contract for vtxo");
        var parsedContract = ArkContract.Parse(contract.Type, contract.AdditionalData, network);
        if (parsedContract is null)
            throw new UnableToSignUnknownContracts("Could not parse contract");
        var arkCoin = parsedContract.ToArkCoin(contract.WalletIdentifier, vtxo);
        return await GetPsbtSigner(arkCoin);
    }
}