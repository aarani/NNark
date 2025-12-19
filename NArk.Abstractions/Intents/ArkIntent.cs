using NBitcoin;

namespace NArk.Abstractions.Intents;

public record ArkIntent(
    Guid InternalId,
    string? IntentId,
    string WalletId,
    string SignerDescriptor,
    ArkIntentState State,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string RegisterProof,
    string RegisterProofMessage,
    string DeleteProof,
    string DeleteProofMessage,
    string? BatchId,
    string? CommitmentTransactionId,
    string? CancellationReason,
    OutPoint[] IntentVtxos
);