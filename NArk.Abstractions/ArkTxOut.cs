using NBitcoin;

namespace NArk.Abstractions;

public class ArkTxOut: TxOut
{
    public ArkTxOutType Type { get; set; }
}

public enum ArkTxOutType
{
    Vtxo,
    Onchain
}