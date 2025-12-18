namespace NArk.Abstractions.Wallets;

public record WalletData(
    string WalletIdentifier,
    string WalletFingerprint,
    byte[] WalletPrivateBytes,
    int LastAddressIndex = 0
);