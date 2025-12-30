using NArk.Contracts;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk;

public class ArkUnspendableCoin : Coin
{
    public string WalletIdentifier { get; }
    public ArkContract Contract { get; }
    public DateTimeOffset? ExpiresAt { get; }
    public uint? ExpiresAtHeight { get; }
    public bool Recoverable { get; }

    public ArkUnspendableCoin(string walletIdentifier,
        ArkContract contract,
        DateTimeOffset? expiresAt,
        uint? expiresAtHeight,
        OutPoint outPoint,
        TxOut txOut,
        bool recoverable) : base(outPoint, txOut)
    {
        WalletIdentifier = walletIdentifier;
        Contract = contract;
        ExpiresAt = expiresAt;
        ExpiresAtHeight = expiresAtHeight;
        Recoverable = recoverable;
    }
}