using NArk.Contracts;
using NBitcoin;

namespace NArk;

public class ArkUnspendableCoin : Coin
{
    public string WalletIdentifier { get; }
    public ArkContract Contract { get; }
    public DateTimeOffset Birth { get; }
    public DateTimeOffset? ExpiresAt { get; }
    public uint? ExpiresAtHeight { get; }
    public bool Recoverable { get; }

    public ArkUnspendableCoin(string walletIdentifier,
        ArkContract contract,
        DateTimeOffset birth,
        DateTimeOffset? expiresAt,
        uint? expiresAtHeight,
        OutPoint outPoint,
        TxOut txOut,
        bool recoverable) : base(outPoint, txOut)
    {
        WalletIdentifier = walletIdentifier;
        Contract = contract;
        Birth = birth;
        ExpiresAt = expiresAt;
        ExpiresAtHeight = expiresAtHeight;
        Recoverable = recoverable;
    }
}