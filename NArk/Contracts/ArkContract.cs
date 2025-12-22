using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Extensions;
using NArk.Scripts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Contracts;

public abstract class ArkContract(OutputDescriptor server)
{
    private static readonly List<IArkContractParser> Parsers = [];

    static ArkContract()
    {
        Parsers.Add(new GenericArkContractParser(ArkPaymentContract.ContractType, ArkPaymentContract.Parse));
        Parsers.Add(new GenericArkContractParser(HashLockedArkPaymentContract.ContractType, HashLockedArkPaymentContract.Parse));
        Parsers.Add(new GenericArkContractParser(VHTLCContract.ContractType, VHTLCContract.Parse));
        Parsers.Add(new GenericArkContractParser(ArkNoteContract.ContractType, ArkNoteContract.Parse));
    }


    public static ArkContract? Parse(string contract, Network network)
    {
        if (!contract.StartsWith("arkcontract"))
        {
            throw new ArgumentException("Invalid contract format. Must start with 'arkcontract'");
        }

        var contractData = IArkContractParser.GetContractData(contract);
        contractData.TryGetValue("arkcontract", out var contractType);

        return
            !string.IsNullOrEmpty(contractType) ?
                Parse(contractType, contractData, network) :
                throw new ArgumentException("Contract type is missing in the contract data");
    }

    public static ArkContract? Parse(string type, Dictionary<string, string> contractData, Network network)
    {
        return Parsers.FirstOrDefault(parser => parser.Type == type)?
            .Parse(contractData, network); // Ensure the Payment parser is registered
    }

    public abstract string Type { get; }

    public OutputDescriptor Server { get; } = server;

    public ArkAddress GetArkAddress()
    {
        var spendInfo = GetTaprootSpendInfo();
        return new ArkAddress(
            ECXOnlyPubKey.Create(spendInfo.OutputPubKey.ToBytes()),
            Server.ToXOnlyPubKey() ?? throw new InvalidOperationException("Server key is required for address generation")
        );
    }

    public virtual TaprootSpendInfo GetTaprootSpendInfo()
    {
        var builder = GetTapScriptList().WithTree();
        return builder.Finalize(new TaprootInternalPubKey(Constants.UnspendableKey.ToECXOnlyPubKey().ToBytes()));
    }

    public virtual TapScript[] GetTapScriptList()
    {
        var leaves = GetScriptBuilders().ToArray();
        if (!leaves.OfType<CollaborativePathArkTapScript>().Any())
            throw new ArgumentException("At least one collaborative path is required");
        if (!leaves.OfType<UnilateralPathArkTapScript>().Any())
            throw new ArgumentException("At least one unilateral path is required");
        if (leaves.Any(x => x is not CollaborativePathArkTapScript && x is not UnilateralPathArkTapScript))
            throw new ArgumentException("Only collaborative and unilateral paths are allowed");

        return leaves.Select(x => x.Build()).ToArray();
    }

    public override string ToString()
    {
        var contractData = GetContractData();
        contractData.Remove("arkcontract");
        var dataString = string.Join("&", contractData.Select(kvp => $"{kvp.Key}={kvp.Value}"));

        return $"arkcontract={Type}&{dataString}";
    }

    public ArkContractEntity ToEntity(string walletIdentifier, DateTimeOffset? createdAt = null, bool isActive = true)
    {
        return new ArkContractEntity(
            GetArkAddress().ScriptPubKey.ToHex(),
            isActive,
            Type,
            GetContractData(),
            walletIdentifier,
            createdAt ?? DateTimeOffset.UtcNow
        );
    }

    protected abstract IEnumerable<ScriptBuilder> GetScriptBuilders();
    protected abstract Dictionary<string, string> GetContractData();
    public abstract ArkCoin ToArkCoin(string walletIdentifier, ArkVtxo vtxo);
}