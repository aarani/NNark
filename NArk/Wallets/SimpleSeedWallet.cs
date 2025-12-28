using System.Text;
using NArk.Abstractions.Wallets;
using NArk.Helpers;
using NArk.Transport;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Wallets;

public class SimpleSeedWallet(IClientTransport clientTransport, IWalletStorage walletStorage) : IWallet
{
    public async Task CreateNewWallet(string walletIdentifier, CancellationToken cancellationToken = default)
    {
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
        var fingerprint = mnemonic.DeriveExtKey().GetPublicKey().GetHDFingerPrint();
        await walletStorage.SaveWallet(walletIdentifier,
            new ArkWallet(walletIdentifier, fingerprint.ToString(), Encoding.UTF8.GetBytes(mnemonic.ToString())),
            fingerprint.ToString(), cancellationToken);
    }

    public async Task<ISigningEntity> GetNewSigningEntity(string walletIdentifier,
        CancellationToken cancellationToken = default)
    {
        var network = (await clientTransport.GetServerInfoAsync(cancellationToken)).Network;
        var walletData = await walletStorage.LoadWallet(walletIdentifier, cancellationToken);
        var mnemonic = new Mnemonic(Encoding.UTF8.GetString(walletData.WalletPrivateBytes));
        var extKey = mnemonic.DeriveExtKey();
        var signer = new HdSigningEntity(extKey, network, walletData.LastAddressIndex);
        await walletStorage.SaveWallet(walletIdentifier,
            walletData with { LastAddressIndex = walletData.LastAddressIndex + 1 },
            cancellationToken: cancellationToken);
        return signer;
    }

    public async Task<ISigningEntity> FindSigningEntity(OutputDescriptor outputDescriptor,
        CancellationToken cancellationToken = default)
    {
        var walletId = OutputDescriptorHelpers.Extract(outputDescriptor).WalletId;
        var walletData = await walletStorage.LoadWallet(walletId, cancellationToken);
        var mnemonic = new Mnemonic(Encoding.UTF8.GetString(walletData.WalletPrivateBytes));
        var extKey = mnemonic.DeriveExtKey();
        var signer = new HdSigningEntity(extKey, outputDescriptor);
        return signer;
    }

    private class HdSigningEntity(ExtKey extKey, OutputDescriptor descriptor) : ISigningEntity
    {
        internal HdSigningEntity(ExtKey extKey, Network network, int index) :
            this(extKey, GetDescriptorFromIndex(extKey, network, index))
        {
        }

        public async Task<Dictionary<string, string>> GetMetadata(CancellationToken cancellationToken = default)
        {
            return
                new Dictionary<string, string>
                {
                    { "Descriptor", extKey.GetPublicKey().ToHex() },
                    { "Fingerprint", await GetFingerprint(cancellationToken) }
                };
        }

        public Task<string> GetFingerprint(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(extKey.GetPublicKey().GetHDFingerPrint().ToString());
        }

        public Task<OutputDescriptor> GetOutputDescriptor(CancellationToken cancellationToken = default) =>
            Task.FromResult(descriptor);

        public Task<ECPubKey> GetPublicKey(CancellationToken cancellationToken = default)
        {
            var info = OutputDescriptorHelpers.Extract(descriptor);
            return Task.FromResult(info.PubKey!);
        }

        private static OutputDescriptor GetDescriptorFromIndex(ExtKey extKey, Network network, int index)
        {
            var fingerprint = extKey.GetPublicKey().GetHDFingerPrint();
            var coinType = network.ChainName == ChainName.Mainnet ? "0" : "1";

            // BIP-86 Taproot: m/86'/coin'/0'
            var accountKeyPath = new KeyPath($"m/86'/{coinType}'/0'");
            var accountXpriv = extKey.Derive(accountKeyPath);
            var accountXpub = accountXpriv.Neuter().GetWif(network).ToWif();

            // Descriptor format: tr([fingerprint/86'/coin'/0']xpub/0/*)
            var descriptor = $"tr([{fingerprint}/86'/{coinType}'/0']{accountXpub}/0/*)";

            return OutputDescriptor.Parse(descriptor.Replace("/*", $"/{index}"), network);
        }

        public async Task<SignResult> SignData(uint256 data, CancellationToken cancellationToken = default)
        {
            var key = await DerivePrivateKey(cancellationToken);
            var sig = key.SignBIP340(data.ToBytes());
            return new SignResult(sig, key.CreateXOnlyPubKey());
        }

        public Task<ECPrivKey> DerivePrivateKey(CancellationToken cancellationToken = default)
        {
            var info = OutputDescriptorHelpers.Extract(descriptor);
            return Task.FromResult(ECPrivKey.Create(extKey.Derive(info.FullPath!).PrivateKey.ToBytes()));
        }

        public async Task<MusigPartialSignature> SignMusig(MusigContext context,
            MusigPrivNonce nonce,
            CancellationToken cancellationToken = default)
        {
            // Create MUSIG2 partial signature using the private key and nonce
            return context.Sign(await DerivePrivateKey(cancellationToken), nonce);
        }
    }
}