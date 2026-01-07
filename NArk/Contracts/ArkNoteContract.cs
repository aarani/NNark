using NArk.Abstractions.Contracts;
using NArk.Abstractions.Scripts;
using NArk.Enums;
using NArk.Scripts;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace NArk.Contracts;

public class ArkNoteContract(byte[] preimage) : HashLockedArkPaymentContract(null!, new Sequence(), null, preimage, HashLockTypeOption.Sha256)
{
    public OutPoint Outpoint => new(new uint256(Hash), 0);

    public override string Type => ContractType;
    public new const string ContractType = "arknote";

    protected override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        yield return new HashLockTapScript(Hash, HashLockTypeOption.Sha256);
    }

    public override TapScript[] GetTapScriptList()
    {
        //we override to remove the checks.
        var leaves = GetScriptBuilders().ToArray();
        return leaves.Select(x => x.Build()).ToArray();
    }

    protected override Dictionary<string, string> GetContractData()
    {
        var data = new Dictionary<string, string>
        {
            ["preimage"] = Encoders.Hex.EncodeData(Preimage)
        };
        return data;
    }

    public new static ArkContract Parse(Dictionary<string, string> arg, Network network)
    {
        var preimage = Encoders.Hex.DecodeData(arg["preimage"]);
        return new ArkNoteContract(preimage);
    }
}