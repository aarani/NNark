using NBitcoin;
using NBitcoin.JsonConverters;

namespace NArk.Abstractions;

public class ArkCoinLite(
    string walletIdentifier,
    OutPoint outPoint,
    TxOut txOut,
    bool recoverable,
    DateTimeOffset birth,
    uint? expiryAtHeight,
    DateTimeOffset? expiryAt,
    bool isNote) : Coin(outPoint, txOut)
{
    public string WalletIdentifier { get; } = walletIdentifier;
    public bool Recoverable { get; } = recoverable;
    public DateTimeOffset Birth { get; } = birth;
    public uint? ExpiryAtHeight { get; } = expiryAtHeight;
    public DateTimeOffset? ExpiryAt { get; } = expiryAt;
    public bool IsNote { get; } = isNote;

    public double GetRawExpiry()
    {
        if (ExpiryAt is not null)
        {
            return ExpiryAt.Value.ToUnixTimeSeconds();
        }

        if (ExpiryAtHeight is not null)
        {
            return ExpiryAtHeight.Value;
        }

        return 0;
    }
}