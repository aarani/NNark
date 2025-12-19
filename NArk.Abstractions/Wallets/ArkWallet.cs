namespace NArk.Abstractions.Wallets;

public record ArkWallet(
    string WalletIdentifier,
    string WalletFingerprint,
    byte[] WalletPrivateBytes,
    int LastAddressIndex = 0
);