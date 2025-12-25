namespace NArk.Swaps.Models;

public record ArkSwap(
    string SwapId,
    string WalletId,
    ArkSwapType SwapType,
    string Invoice,
    long ExpectedAmount,
    string ContractScript,
    ArkSwapStatus Status,
    string? FailReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Hash);
public enum ArkSwapStatus
{
    Pending,
    Settled,
    Failed,
    PendingRefund,
    Refunded,
    Unknown
}

public enum ArkSwapType
{
    ReverseSubmarine,
    Submarine
}