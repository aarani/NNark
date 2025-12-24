using NBitcoin;

namespace NArk.Abstractions;

public class ArkCoinLite(
    string walletIdentifier,
    OutPoint outPoint,
    TxOut txOut,
    bool recoverable,
    uint? expiryAtHeight,
    DateTimeOffset? expiryAt) : Coin(outPoint, txOut)
{
    public string WalletIdentifier { get; } = walletIdentifier;
    public bool Recoverable { get; } = recoverable;
    public uint? ExpiryAtHeight { get; } = expiryAtHeight;
    public DateTimeOffset? ExpiryAt { get; } = expiryAt;
}