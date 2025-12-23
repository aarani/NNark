using NArk.Abstractions.Wallets;
using NBitcoin;

namespace NArk.Abstractions;

public class ArkCoinLite(
    string walletIdentifier,
    OutPoint outPoint,
    TxOut txOut,
    bool Recoverable,
    uint? ExpiryAtHeight,
    DateTimeOffset? ExpiryAt) : Coin(outPoint, txOut)
{
    public string WalletIdentifier { get; } = walletIdentifier;
    public bool Recoverable { get; } = Recoverable;
    public uint? ExpiryAtHeight { get; } = ExpiryAtHeight;
    public DateTimeOffset? ExpiryAt { get; } = ExpiryAt;
}