using NArk.Contracts;

namespace NArk.Events;

public record NewContractActionEvent(
    ArkContract Contract,
    string WalletId
);