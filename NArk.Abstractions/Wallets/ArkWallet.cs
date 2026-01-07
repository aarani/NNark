using NBitcoin.Scripting;

namespace NArk.Abstractions.Wallets;

public record ArkWallet(
    string WalletIdentifier,
    string WalletFingerprint,
    string WalletDescriptor,
    int LastAddressIndex = 0
);