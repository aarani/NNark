using System.Collections.Concurrent;
using NArk.Abstractions.Wallets;
using NBitcoin;

namespace NArk.Tests.End2End.TestPersistance;

internal class InMemoryKeyStorage : IKeyStorage
{
    private ConcurrentDictionary<string, ExtKey> _keys = [];
    private ConcurrentDictionary<string, string> _fingerprintToWalletId = [];

    public async Task AddPrivateKeyAsync(string walletIdentifier, ExtKey extKey, CancellationToken cancellationToken = default)
    {
        _keys[walletIdentifier] = extKey;
        _fingerprintToWalletId[extKey.GetPublicKey().GetHDFingerPrint().ToString()] = walletIdentifier;
    }

    public async Task<ExtKey> GetPrivateKeyAsync(string walletIdentifier, CancellationToken cancellationToken = default)
    {
        if (_keys.TryGetValue(walletIdentifier, out var key)) return key;
        if (_fingerprintToWalletId.TryGetValue(walletIdentifier, out var value) &&
            _keys.TryGetValue(value, out var extKey)) return extKey;
        throw new Exception("Unknown key");
    }
}