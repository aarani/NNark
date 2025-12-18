using System.Text;
using NArk.Abstractions.Wallets;
using NArk.Helpers;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Wallets;

public class SimpleSeedWallet(Network network, IWalletStorage walletStorage): IWallet
{
    public async Task CreateNewWallet(string walletIdentifier)
    {
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
        var fingerprint = mnemonic.DeriveExtKey().GetPublicKey().GetHDFingerPrint();
        await walletStorage.SaveWallet(walletIdentifier, new WalletData(walletIdentifier, fingerprint.ToString(), Encoding.UTF8.GetBytes(mnemonic.ToString())), fingerprint.ToString());
    }

    public async Task<ISigningEntity> GetSigningEntity(string walletIdentifier)
    {
        var walletData = await walletStorage.LoadWallet(walletIdentifier);
        var mnemonic = new Mnemonic(Encoding.UTF8.GetString(walletData.WalletPrivateBytes));
        var extKey = mnemonic.DeriveExtKey();
        var signer = new HdSigningEntity(extKey, network, walletData.LastAddressIndex);
        await walletStorage.SaveWallet(walletIdentifier, walletData with { LastAddressIndex = walletData.LastAddressIndex + 1 });
        return signer;
    }
    
    private class HdSigningEntity(ExtKey extKey, Network network, int index): ISigningEntity
    {
        public Dictionary<string, string> GetMetadata()
        {
            return new Dictionary<string, string>
            {
                { "Descriptor", extKey.GetPublicKey().ToHex() },
                { "Fingerprint", GetFingerprint() }
            };
        }

        private string GetFingerprint()
        {
            return extKey.GetPublicKey().GetHDFingerPrint().ToString();
        }

        private OutputDescriptor GetDescriptor()
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

        public SignResult SignData(uint256 data)
        {
            var key = DerivePrivateKey(GetDescriptor());
            var sig = key.SignBIP340(data.ToBytes());
            return new SignResult(sig, key.CreateXOnlyPubKey());
        }

        private ECPrivKey DerivePrivateKey(OutputDescriptor descriptor)
        {
            var info = OutputDescriptorHelper.Extract(descriptor);
            var walletId =
                info.AccountPath?.MasterFingerprint.ToString() ?? 
                    Convert.ToHexStringLower(info.XOnlyPubKey.ToBytes());
            
            if (walletId != GetFingerprint())
            {
                throw new Exception("invalid descriptor, cannot sign");
            }

            return ECPrivKey.Create(extKey.Derive(info.FullPath!).PrivateKey.ToBytes());
        }
    }
}