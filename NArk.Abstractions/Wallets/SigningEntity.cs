using NBitcoin;

namespace NArk.Abstractions.Wallets;

public interface ISigningEntity
{
    Task<Dictionary<string, string>> GetMetadata();
    Task<string> GetFingerprint();
    Task<SignResult> SignData(uint256 hash);
}