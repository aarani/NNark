using NBitcoin;

namespace NArk;

public record ArkVtxo(
    string Script,
    string TransactionId,
    uint TransactionOutputIndex,
    ulong Amount,
    string? SpentByTransactionId,
    string? SettledByTransactionId,
    bool Recoverable,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ExpiresAt,
    uint? ExpiresAtHeight)
{
    public bool IsExpired(long currentTimestamp, uint currentBlockHeight)
    {
        if (ExpiresAt is not null && DateTimeOffset.FromUnixTimeSeconds(currentTimestamp) >= ExpiresAt)
            return true;
        if (ExpiresAtHeight is not null && currentBlockHeight >= ExpiresAtHeight)
            return true;
        return false;
    }
    
    public ICoinable ToCoin()
    { 
        var outpoint = new OutPoint(new uint256(TransactionId), TransactionOutputIndex);
        var txOut = new TxOut(Money.Satoshis(Amount), NBitcoin.Script.FromHex(Script));
        return new Coin(outpoint, txOut);
    }

    public bool IsSpent()
    {
        return !string.IsNullOrEmpty(SpentByTransactionId) || !string.IsNullOrEmpty(SettledByTransactionId);
    }
}