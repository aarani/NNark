namespace NArk.Abstractions.Intents;

public record ArkIntentSpec(
    ArkCoinLite[] Coins,
    ArkTxOut[] Outputs,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil
);