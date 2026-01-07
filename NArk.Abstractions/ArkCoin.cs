using NArk.Abstractions.Contracts;
using NArk.Abstractions.Helpers;
using NArk.Abstractions.Scripts;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk.Abstractions;

public class ArkCoin : Coin
{
    public ArkCoin(string walletIdentifier,
        ArkContract contract,
        DateTimeOffset birth,
        DateTimeOffset? expiresAt,
        uint? expiresAtHeight,
        OutPoint outPoint,
        TxOut txOut,
        OutputDescriptor signerDescriptor,
        ScriptBuilder spendingScriptBuilder,
        WitScript? spendingConditionWitness,
        LockTime? lockTime,
        Sequence? sequence,
        bool recoverable) : base(outPoint, txOut)
    {
        WalletIdentifier = walletIdentifier;
        Contract = contract;
        Birth = birth;
        ExpiresAt = expiresAt;
        ExpiresAtHeight = expiresAtHeight;
        SignerDescriptor = signerDescriptor;
        SpendingScriptBuilder = spendingScriptBuilder;
        SpendingConditionWitness = spendingConditionWitness;
        LockTime = lockTime;
        Sequence = sequence;
        Recoverable = recoverable;

        if (sequence is null && spendingScriptBuilder.BuildScript().Contains(OpcodeType.OP_CHECKSEQUENCEVERIFY))
        {
            throw new InvalidOperationException("Sequence is required");
        }
    }

    public ArkCoin(ArkCoin other) : this(
        other.WalletIdentifier, other.Contract, other.Birth, other.ExpiresAt, other.ExpiresAtHeight, other.Outpoint.Clone(), other.TxOut.Clone(), other.SignerDescriptor,
        other.SpendingScriptBuilder, other.SpendingConditionWitness?.Clone(), other.LockTime, other.Sequence,
        other.Recoverable)
    {
    }

    public string WalletIdentifier { get; }
    public ArkContract Contract { get; }
    public DateTimeOffset Birth { get; }
    public DateTimeOffset? ExpiresAt { get; }
    public uint? ExpiresAtHeight { get; }
    public OutputDescriptor SignerDescriptor { get; }
    public ScriptBuilder SpendingScriptBuilder { get; }
    public WitScript? SpendingConditionWitness { get; }
    public LockTime? LockTime { get; }
    public Sequence? Sequence { get; }
    public bool Recoverable { get; }

    public TapScript SpendingScript => SpendingScriptBuilder.Build();

    public PSBTInput? FillPsbtInput(PSBT psbt)
    {
        var psbtInput = psbt.Inputs.FindIndexedInput(Outpoint);
        if (psbtInput is null)
        {
            return null;
        }

        psbtInput.SetArkFieldTapTree(Contract.GetTapScriptList());
        psbtInput.SetTaprootLeafScript(Contract.GetTaprootSpendInfo(), SpendingScript);
        if (SpendingConditionWitness is not null)
        {
            psbtInput.SetArkFieldConditionWitness(SpendingConditionWitness);
        }

        return psbtInput;
    }
    
    public double GetRawExpiry()
    {
        if (ExpiresAt is not null)
        {
            return ExpiresAt.Value.ToUnixTimeSeconds();
        }

        if (ExpiresAtHeight is not null)
        {
            return ExpiresAtHeight.Value;
        }

        return 0;
    }
}