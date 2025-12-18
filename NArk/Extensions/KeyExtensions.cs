using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Extensions;

public static class KeyExtensions
{
    public static ECXOnlyPubKey ToECXOnlyPubKey(this string pubKeyHex)
    {
        var pubKey = new PubKey(pubKeyHex);
        return pubKey.ToECXOnlyPubKey();
    }

    public static ECXOnlyPubKey ToECXOnlyPubKey(this byte[] pubKeyBytes)
    {
        var pubKey = new PubKey(pubKeyBytes);
        return pubKey.ToECXOnlyPubKey();
    }

    public static ECXOnlyPubKey ToECXOnlyPubKey(this PubKey pubKey)
    {
        var xOnly = pubKey.TaprootInternalKey.ToBytes();
        return ECXOnlyPubKey.Create(xOnly);
    }

    public static string ToHex(this ECXOnlyPubKey value)
    {
        return Convert.ToHexString(value.ToBytes()).ToLowerInvariant();
    }

    public static string ToHex(this ECPubKey value)
    {
        return Convert.ToHexString(value.ToBytes()).ToLowerInvariant();
    }

    public static Key ToKey(this ECPrivKey key)
    {
        var bytes = new Span<byte>();
        key.WriteToSpan(bytes);
        return new Key(bytes.ToArray());
    }

    public static ECPrivKey ToKey(this Key key)
    {
        return ECPrivKey.Create(key.ToBytes());
    }

    public static ECXOnlyPubKey GetXOnlyPubKey(this Key key)
    {
        return key.ToKey().CreateXOnlyPubKey();
    }

    public static ECPubKey ToPubKey(this OutputDescriptor descriptor)
    {
        if (descriptor is not OutputDescriptor.Tr trOutputDescriptor)
        {
            throw new ArgumentException("the output descriptor must be tr", nameof(descriptor));
        }

        byte[]? bytes;
        if (trOutputDescriptor.InnerPubkey is PubKeyProvider.Const constPubKeyProvider)
        {
            if (constPubKeyProvider.Xonly)
                throw new ArgumentException("the output descriptor only describe an xonly public key",
                    nameof(descriptor));
            bytes = constPubKeyProvider.Pk.ToBytes();
        }
        else
        {
            bytes = trOutputDescriptor.InnerPubkey.GetPubKey(0, id => null).ToBytes();
        }

        return ECPubKey.Create(bytes);
    }

    public static ECXOnlyPubKey ToXOnlyPubKey(this OutputDescriptor descriptor)
    {
        if (descriptor is not OutputDescriptor.Tr trOutputDescriptor)
        {
            throw new ArgumentException("the output descriptor must be tr", nameof(descriptor));
        }

        if (trOutputDescriptor.InnerPubkey is PubKeyProvider.Const { Xonly: true } constPubKeyProvider)
            return ECXOnlyPubKey.Create(constPubKeyProvider.Pk.ToBytes()[1..]);

        return descriptor.ToPubKey().ToXOnlyPubKey();
    }
    
    public static OutputDescriptor ParseOutputDescriptor(string str, Network network)
    {
        if (!HexEncoder.IsWellFormed(str))
            return OutputDescriptor.Parse(str, network);
        
        var bytes = Convert.FromHexString(str);
        if (bytes.Length != 32 && bytes.Length != 33)
        {
            throw new ArgumentException("the string must be 32/33 bytes long", nameof(str));
        }

        return OutputDescriptor.Parse($"tr({str})", network);

    }
}