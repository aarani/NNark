using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Services;
using NArk.Transport;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Wallets;

public class SimpleSeedWallet(ISafetyService safetyService, IClientTransport clientTransport, IWalletStorage walletStorage, IKeyStorage keyStorage) : IWallet
{
    public async Task CreateNewWallet(string walletIdentifier, CancellationToken cancellationToken = default)
    {
        var serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        await using var @lock = await safetyService.LockKeyAsync($"wallet::{walletIdentifier}", cancellationToken);
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
        var extKey = mnemonic.DeriveExtKey();
        var fingerprint = extKey.GetPublicKey().GetHDFingerPrint();
        var coinType = serverInfo.Network.ChainName == ChainName.Mainnet ? "0" : "1";

        // BIP-86 Taproot: m/86'/coin'/0'
        var accountKeyPath = new KeyPath($"m/86'/{coinType}'/0'");
        var accountXpriv = extKey.Derive(accountKeyPath);
        var accountXpub = accountXpriv.Neuter().GetWif(serverInfo.Network).ToWif();

        // Descriptor format: tr([fingerprint/86'/coin'/0']xpub/0/*)
        var descriptor = $"tr([{fingerprint}/86'/{coinType}'/0']{accountXpub}/0/*)";

        await keyStorage.AddPrivateKeyAsync(walletIdentifier, extKey, cancellationToken);
        await walletStorage.SaveWallet(walletIdentifier,
            new ArkWallet(walletIdentifier, fingerprint.ToString(), descriptor),
            fingerprint.ToString(), cancellationToken);
    }

    public async Task<string> GetWalletFingerprint(string walletIdentifier, CancellationToken cancellationToken = default)
    {
        var wallet = await walletStorage.LoadWallet(walletIdentifier, cancellationToken);
        return wallet.WalletFingerprint;
    }

    public async Task<OutputDescriptor> GetNewSigningDescriptor(string walletIdentifier,
        CancellationToken cancellationToken = default)
    {
        static OutputDescriptor GetDescriptorFromIndex(Network network, string descriptor, int index)
        {
            return OutputDescriptor.Parse(descriptor.Replace("/*", $"/{index}"), network);
        }

        var network = (await clientTransport.GetServerInfoAsync(cancellationToken)).Network;
        await using var @lock = await safetyService.LockKeyAsync($"wallet::{walletIdentifier}", cancellationToken);
        var walletData = await walletStorage.LoadWallet(walletIdentifier, cancellationToken);
        await walletStorage.SaveWallet(walletIdentifier,
            walletData with { LastAddressIndex = walletData.LastAddressIndex + 1 },
            cancellationToken: cancellationToken);
        return GetDescriptorFromIndex(network, walletData.WalletDescriptor, walletData.LastAddressIndex);
    }


}