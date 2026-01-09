using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Extensions;
using NArk.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Wallets;

public class SingleKeyWallet(ISafetyService safetyService, IClientTransport clientTransport, IWalletStorage walletStorage, IKeyStorage keyStorage) : IWallet
{
    public async Task CreateNewWallet(string walletIdentifier, CancellationToken cancellationToken = default)
    {
        await using var @lock = await safetyService.LockKeyAsync($"wallet::{walletIdentifier}", cancellationToken);
        var keyMaterial = RandomUtils.GetBytes(32);
        var key = ECPrivKey.Create(keyMaterial);
        var fingerprint = key.CreateXOnlyPubKey(.ToBytes().ToHexStringLower();
        
        var descriptor = $"tr([{key.CreatePubKey().ToBytes().ToHexStringLower()})";

        await keyStorage.AddPrivateKeyAsync(walletIdentifier, extKey, cancellationToken);
        await walletStorage.SaveWallet(walletIdentifier,
            new ArkWallet(walletIdentifier, fingerprint, descriptor), cancellationToken);
    }

    public async Task<string> GetWalletFingerprint(string walletIdentifier, CancellationToken cancellationToken = default)
    {
        var wallet = await walletStorage.LoadWallet(walletIdentifier, cancellationToken);
        return wallet.WalletFingerprint;
    }

    public async Task<OutputDescriptor> DeriveNextDescriptor(string walletIdentifier,
        CancellationToken cancellationToken = default)
    {
        var network = (await clientTransport.GetServerInfoAsync(cancellationToken)).Network;
        var walletData = await walletStorage.LoadWallet(walletIdentifier, cancellationToken);
        return OutputDescriptor.Parse(walletData.WalletDescriptor, network);
    }
}