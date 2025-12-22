using System.Collections.Concurrent;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;

namespace NArk.Tests.End2End;

public class InMemoryWalletStorage: IWalletStorage
{
    private readonly ConcurrentDictionary<string, ArkWallet> _wallets = new();
    
    public async Task<IReadOnlySet<ArkWallet>> LoadAllWallets()
    {
        return _wallets.Values.ToHashSet();
    }

    public async Task<ArkWallet> LoadWallet(string walletIdentifierOrFingerprint)
    {
        if (_wallets.TryGetValue(walletIdentifierOrFingerprint, out var wallet))
            return wallet;

        return 
            _wallets
                .Values
                .First(w => w.WalletFingerprint == walletIdentifierOrFingerprint);
    }

    public async Task SaveWallet(string walletId, ArkWallet arkWallet, string? walletFingerprint = null)
    {
        _wallets[walletId] = arkWallet;
    }
}