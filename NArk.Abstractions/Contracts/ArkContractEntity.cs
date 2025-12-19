namespace NArk.Abstractions.Contracts;

public record ArkContractEntity(
    string Script,
    bool Important,
    string Type,
    Dictionary<string, string> AdditionalData,
    string WalletIdentifier,
    DateTimeOffset CreatedAt
);