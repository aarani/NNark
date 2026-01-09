using System.Collections.Concurrent;
using NArk.Abstractions.Wallets;

namespace NArk.Tests.End2End.TestPersistance;

public class InMemoryWalletStorage : IWalletStorage
{
    private readonly ConcurrentDictionary<string, ArkWallet> _wallets = new();

    public Task<IReadOnlySet<ArkWallet>> LoadAllWallets(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlySet<ArkWallet>>(_wallets.Values.ToHashSet());
    }

    public Task<ArkWallet> LoadWallet(string walletIdentifierOrFingerprint, CancellationToken cancellationToken = default)
    {
        if (_wallets.TryGetValue(walletIdentifierOrFingerprint, out var wallet))
            return Task.FromResult(wallet);

        return
            Task.FromResult(_wallets
                .Values
                .First(w => w.WalletFingerprint == walletIdentifierOrFingerprint));
    }

    public Task SaveWallet(string walletId, ArkWallet arkWallet,
        CancellationToken cancellationToken = default)
    {
        _wallets[walletId] = arkWallet;
        return Task.CompletedTask;
    }
}