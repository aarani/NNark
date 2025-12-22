namespace NArk.Abstractions.Contracts;

public record ArkContractEntity(
    string Script,
    bool Important,
    string Type,
    Dictionary<string, string> AdditionalData,
    string WalletIdentifier,
    DateTimeOffset CreatedAt
)
{
    private sealed class ScriptEqualityComparer : IEqualityComparer<ArkContractEntity>
    {
        public bool Equals(ArkContractEntity? x, ArkContractEntity? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null) return false;
            if (y is null) return false;
            if (x.GetType() != y.GetType()) return false;
            return x.Script == y.Script;
        }

        public int GetHashCode(ArkContractEntity obj)
        {
            return obj.Script.GetHashCode();
        }
    }

    public static IEqualityComparer<ArkContractEntity> ScriptComparer { get; } = new ScriptEqualityComparer();
}