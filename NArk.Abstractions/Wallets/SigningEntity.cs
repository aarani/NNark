using NBitcoin;

namespace NArk.Abstractions.Wallets;

public interface ISigningEntity
{
    Dictionary<string, string> GetMetadata();
    SignResult SignData(uint256 hash);
}