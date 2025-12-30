using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Services;
using NArk.Transport;
using NSubstitute;

namespace NArk.Tests;

public class VtxoSynchronizationServiceTests
{
    [Test]
    public async Task VtxoSynchronizationService_ReceivesContractsList()
    {
        var walletStorage = Substitute.For<IWalletStorage>();
        var vtxoStorage = Substitute.For<IVtxoStorage>();
        var contractStorage = Substitute.For<IContractStorage>();
        var arkClientTransport = Substitute.For<IClientTransport>();

        walletStorage.LoadAllWallets(Arg.Any<CancellationToken>())
            .Returns(new HashSet<ArkWallet>([new ArkWallet("wallet1", "wallet-fingerprint", [])]));
        contractStorage.LoadAllContractsByWallet("wallet1", Arg.Any<CancellationToken>()).Returns(new HashSet<ArkContractEntity>());

        var service = new VtxoSynchronizationService(walletStorage, vtxoStorage, contractStorage, arkClientTransport);
        await service.StartAsync(CancellationToken.None);

        walletStorage.Received(1).LoadAllWallets(Arg.Any<CancellationToken>());
        contractStorage.Received(1).LoadAllContractsByWallet(Arg.Is("wallet1"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task VtxoSynchronizationService_ReceivesVtxosList()
    {
        var walletStorage = Substitute.For<IWalletStorage>();
        var vtxoStorage = Substitute.For<IVtxoStorage>();
        var contractStorage = Substitute.For<IContractStorage>();
        var arkClientTransport = Substitute.For<IClientTransport>();

        walletStorage.LoadAllWallets(Arg.Any<CancellationToken>())
            .Returns(new HashSet<ArkWallet>([new ArkWallet("wallet1", "wallet-fingerprint", [])]));
        contractStorage.LoadAllContractsByWallet("wallet1", Arg.Any<CancellationToken>()).Returns(new HashSet<ArkContractEntity>
        {
            new(
                "script1",
                true,
                "contract-type",
                new Dictionary<string, string>(),
                "wallet1",
                DateTimeOffset.Now
            )
        });
        arkClientTransport
            .GetVtxoByScriptsAsSnapshot(
                Arg.Is<IReadOnlySet<string>>(x => x.SetEquals(new List<string> { "script1" })),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                List<ArkVtxo> test =
                [
                    new(
                        "script1",
                        "txid",
                        0,
                        1000,
                        "spender",
                        "settler",
                        false,
                        DateTimeOffset.Now,
                        null,
                        1024
                    )
                ];
                return test.ToAsyncEnumerable();
            });

        await using (var service =
                     new VtxoSynchronizationService(walletStorage, vtxoStorage, contractStorage, arkClientTransport))
            await service.StartAsync(CancellationToken.None);
        _ = walletStorage.Received(1).LoadAllWallets(Arg.Any<CancellationToken>());
        _ = contractStorage.Received(1).LoadAllContractsByWallet(Arg.Is("wallet1"), Arg.Any<CancellationToken>());
        _ = arkClientTransport.Received(1).GetVtxoByScriptsAsSnapshot(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task VtxoSynchronizationService_UpdateOnStreamTriggersSecondPoll()
    {
        var walletStorage = Substitute.For<IWalletStorage>();
        var vtxoStorage = Substitute.For<IVtxoStorage>();
        var contractStorage = Substitute.For<IContractStorage>();
        var arkClientTransport = Substitute.For<IClientTransport>();

        walletStorage.LoadAllWallets(Arg.Any<CancellationToken>())
            .Returns(new HashSet<ArkWallet>([new ArkWallet("wallet1", "wallet-fingerprint", [])]));
        contractStorage.LoadAllContractsByWallet("wallet1", Arg.Any<CancellationToken>()).Returns(new HashSet<ArkContractEntity>
        {
            new(
                "script1",
                true,
                "contract-type",
                new Dictionary<string, string>(),
                "wallet1",
                DateTimeOffset.Now
            )
        });
        arkClientTransport
            .GetVtxoByScriptsAsSnapshot(
                Arg.Is<IReadOnlySet<string>>(x => x.SetEquals(new List<string> { "script1" })),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                List<ArkVtxo> test =
                [
                    new(
                        "script1",
                        "txid",
                        0,
                        1000,
                        "spender",
                        "settler",
                        false,
                        DateTimeOffset.Now,
                        null,
                        1024
                    )
                ];
                return test.ToAsyncEnumerable();
            });

        arkClientTransport
            .GetVtxoToPollAsStream(Arg.Is<IReadOnlySet<string>>(x => x.SetEquals(new List<string> { "script1" })),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var test = new[] { new HashSet<string> { "script1" } };
                return test.ToAsyncEnumerable();
            });

        await using (var service =
                     new VtxoSynchronizationService(walletStorage, vtxoStorage, contractStorage, arkClientTransport))
            await service.StartAsync(CancellationToken.None);

        _ = walletStorage.Received(1).LoadAllWallets(Arg.Any<CancellationToken>());
        _ = contractStorage.Received(1).LoadAllContractsByWallet(Arg.Is("wallet1"), Arg.Any<CancellationToken>());
        arkClientTransport.Received(2).GetVtxoByScriptsAsSnapshot(
            Arg.Is<IReadOnlySet<string>>(x => x.SetEquals(new List<string> { "script1" })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task VtxoSynchronizationService_ChangeContractTriggersThirdPoll()
    {
        var walletStorage = Substitute.For<IWalletStorage>();
        var vtxoStorage = Substitute.For<IVtxoStorage>();
        var contractStorage = Substitute.For<IContractStorage>();
        var arkClientTransport = Substitute.For<IClientTransport>();

        walletStorage.LoadAllWallets(Arg.Any<CancellationToken>())
            .Returns(new HashSet<ArkWallet>([new ArkWallet("wallet1", "wallet-fingerprint", [])]));
        contractStorage.LoadAllContractsByWallet("wallet1", Arg.Any<CancellationToken>()).Returns(new HashSet<ArkContractEntity>
        {
            new(
                "script1",
                true,
                "contract-type",
                new Dictionary<string, string>(),
                "wallet1",
                DateTimeOffset.Now
            )
        });
        arkClientTransport
            .GetVtxoByScriptsAsSnapshot(
                Arg.Is<IReadOnlySet<string>>(x => x.SetEquals(new List<string> { "script1" })),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                List<ArkVtxo> test =
                [
                    new(
                        "script1",
                        "txid",
                        0,
                        1000,
                        "spender",
                        "settler",
                        false,
                        DateTimeOffset.Now,
                        null,
                        1024
                    )
                ];
                return test.ToAsyncEnumerable();
            });

        arkClientTransport
            .GetVtxoToPollAsStream(Arg.Is<IReadOnlySet<string>>(x => x.SetEquals(new List<string> { "script1" })),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var test = new[] { new HashSet<string> { "script1" } };
                return test.ToAsyncEnumerable();
            });

        await using (var service =
                     new VtxoSynchronizationService(walletStorage, vtxoStorage, contractStorage, arkClientTransport))
        {
            await service.StartAsync(CancellationToken.None);

            contractStorage.LoadAllContractsByWallet("wallet1", Arg.Any<CancellationToken>()).Returns(new HashSet<ArkContractEntity>
            {
                new(
                    "script1",
                    true,
                    "contract-type",
                    new Dictionary<string, string>(),
                    "wallet1",
                    DateTimeOffset.Now
                ),
                new(
                    "script2",
                    true,
                    "contract-type",
                    new Dictionary<string, string>(),
                    "wallet1",
                    DateTimeOffset.Now
                )
            });

            contractStorage.ContractsChanged += Raise.Event<EventHandler<ArkContractEntity>>(null, null);

            await Task.Delay(1000);
        }

        _ = walletStorage.Received(2).LoadAllWallets(Arg.Any<CancellationToken>());
        _ = contractStorage.Received(2).LoadAllContractsByWallet(Arg.Is("wallet1"), Arg.Any<CancellationToken>());
        arkClientTransport.Received(2).GetVtxoByScriptsAsSnapshot(
            Arg.Is<HashSet<string>>(x => x.SetEquals(new List<string> { "script1" })),
            Arg.Any<CancellationToken>());
        arkClientTransport.Received(1).GetVtxoByScriptsAsSnapshot(
            Arg.Is<HashSet<string>>(x => x.SetEquals(new List<string> { "script1", "script2" })),
            Arg.Any<CancellationToken>());
    }
}