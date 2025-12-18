using NArk.Contracts;
using NArk.Helpers;
using NArk.Scripts;
using NBitcoin;

namespace NArk;

public class ArkCoin : Coin
{
    public ArkCoin(ArkContract contract,
        DateTimeOffset? expiresAt,
        uint? expiresAtHeight,
        OutPoint outPoint,
        TxOut txOut,
        ScriptBuilder spendingScriptBuilder,
        WitScript? spendingConditionWitness,
        LockTime? lockTime,
        Sequence? sequence,
        bool recoverable) : base(outPoint, txOut)
    {
        Contract = contract;
        ExpiresAt = expiresAt;
        ExpiresAtHeight = expiresAtHeight;
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

    private ArkContract Contract { get; }
    private DateTimeOffset? ExpiresAt { get; }
    private uint? ExpiresAtHeight { get; }
    private ScriptBuilder SpendingScriptBuilder { get; }
    private WitScript? SpendingConditionWitness { get; }
    private LockTime? LockTime { get; }
    private Sequence? Sequence { get; }
    private bool Recoverable { get; }
    
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
}