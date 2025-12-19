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
    public void VtxoSynchronizationService_ReceivesContractsList()
    {
        var walletStorage = NSubstitute.Substitute.For<IWalletStorage>();
        var vtxoStorage = NSubstitute.Substitute.For<IVtxoStorage>();
        var contractStorage = NSubstitute.Substitute.For<IContractStorage>();
        var arkClientTransport = NSubstitute.Substitute.For<IClientTransport>();

        walletStorage.LoadAllWallets()
            .Returns(new HashSet<ArkWallet>([new ArkWallet("wallet1", "wallet-fingerprint", [])]));
        contractStorage.LoadAllContracts("wallet1").Returns(new HashSet<ArkContractEntity>());

        var service = new VtxoSynchronizationService(walletStorage, vtxoStorage, contractStorage, arkClientTransport);
        service.Start().Wait();

        walletStorage.Received(1).LoadAllWallets();
        contractStorage.Received(1).LoadAllContracts(Arg.Is("wallet1"));
    }

    [Test]
    public async Task VtxoSynchronizationService_ReceivesVtxosList()
    {
        var walletStorage = NSubstitute.Substitute.For<IWalletStorage>();
        var vtxoStorage = NSubstitute.Substitute.For<IVtxoStorage>();
        var contractStorage = NSubstitute.Substitute.For<IContractStorage>();
        var arkClientTransport = NSubstitute.Substitute.For<IClientTransport>();

        walletStorage.LoadAllWallets()
            .Returns(new HashSet<ArkWallet>([new ArkWallet("wallet1", "wallet-fingerprint", [])]));
        contractStorage.LoadAllContracts("wallet1").Returns(new HashSet<ArkContractEntity>()
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
                Arg.Is<IReadOnlySet<string>>(x => x.SetEquals(new List<string>() { "script1" })),
                Arg.Any<CancellationToken>())
            .Returns((c) =>
            {
                List<ArkVtxo> test =
                [
                    new("script1", "txid", 0, 1000, "spender", "settler", false, DateTimeOffset.Now, null, 1024)
                ];
                return test.ToAsyncEnumerable();
            });

        await using var service =
            new VtxoSynchronizationService(walletStorage, vtxoStorage, contractStorage, arkClientTransport);
        service.Start().Wait();
        await service.DisposeAsync();
        walletStorage.Received(1).LoadAllWallets();
        contractStorage.Received(1).LoadAllContracts(Arg.Is("wallet1"));
        arkClientTransport.Received(1).GetVtxoByScriptsAsSnapshot(
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task VtxoSynchronizationService_UpdateOnStreamTriggersSecondPoll()
    {
        var walletStorage = NSubstitute.Substitute.For<IWalletStorage>();
        var vtxoStorage = NSubstitute.Substitute.For<IVtxoStorage>();
        var contractStorage = NSubstitute.Substitute.For<IContractStorage>();
        var arkClientTransport = NSubstitute.Substitute.For<IClientTransport>();

        walletStorage.LoadAllWallets()
            .Returns(new HashSet<ArkWallet>([new ArkWallet("wallet1", "wallet-fingerprint", [])]));
        contractStorage.LoadAllContracts("wallet1").Returns(new HashSet<ArkContractEntity>()
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
                Arg.Is<IReadOnlySet<string>>(x => x.SetEquals(new List<string>() { "script1" })),
                Arg.Any<CancellationToken>())
            .Returns((c) =>
            {
                List<ArkVtxo> test =
                [
                    new("script1", "txid", 0, 1000, "spender", "settler", false, DateTimeOffset.Now, null, 1024)
                ];
                return test.ToAsyncEnumerable();
            });

        arkClientTransport
            .GetVtxoToPollAsStream(Arg.Is<IReadOnlySet<string>>(x => x.SetEquals(new List<string>() { "script1" })),
                Arg.Any<CancellationToken>())
            .Returns((c) =>
            {
                var test = new[] { new HashSet<string>() { "script1" } };
                return test.ToAsyncEnumerable();
            });

        await using var service =
            new VtxoSynchronizationService(walletStorage, vtxoStorage, contractStorage, arkClientTransport);
        service.Start().Wait();
        await service.DisposeAsync();
        walletStorage.Received(1).LoadAllWallets();
        contractStorage.Received(1).LoadAllContracts(Arg.Is("wallet1"));
        arkClientTransport.Received(2).GetVtxoByScriptsAsSnapshot(
            Arg.Is<IReadOnlySet<string>>(x => x.SetEquals(new List<string>() { "script1" })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task VtxoSynchronizationService_ChangeContractTriggersThirdPoll()
    {
        var walletStorage = NSubstitute.Substitute.For<IWalletStorage>();
        var vtxoStorage = NSubstitute.Substitute.For<IVtxoStorage>();
        var contractStorage = NSubstitute.Substitute.For<IContractStorage>();
        var arkClientTransport = NSubstitute.Substitute.For<IClientTransport>();

        walletStorage.LoadAllWallets()
            .Returns(new HashSet<ArkWallet>([new ArkWallet("wallet1", "wallet-fingerprint", [])]));
        contractStorage.LoadAllContracts("wallet1").Returns(new HashSet<ArkContractEntity>()
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
                Arg.Is<IReadOnlySet<string>>(x => x.SetEquals(new List<string>() { "script1" })),
                Arg.Any<CancellationToken>())
            .Returns((c) =>
            {
                List<ArkVtxo> test =
                [
                    new("script1", "txid", 0, 1000, "spender", "settler", false, DateTimeOffset.Now, null, 1024)
                ];
                return test.ToAsyncEnumerable();
            });

        arkClientTransport
            .GetVtxoToPollAsStream(Arg.Is<IReadOnlySet<string>>(x => x.SetEquals(new List<string>() { "script1" })),
                Arg.Any<CancellationToken>())
            .Returns((c) =>
            {
                var test = new[] { new HashSet<string>() { "script1" } };
                return test.ToAsyncEnumerable();
            });

        await using var service =
            new VtxoSynchronizationService(walletStorage, vtxoStorage, contractStorage, arkClientTransport);
        service.Start().Wait();

        contractStorage.LoadAllContracts("wallet1").Returns(new HashSet<ArkContractEntity>()
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

        contractStorage.ContractsChanged += Raise.Event();

        await Task.Delay(1000);

        await service.DisposeAsync();

        walletStorage.Received(2).LoadAllWallets();
        contractStorage.Received(2).LoadAllContracts(Arg.Is("wallet1"));
        arkClientTransport.Received(2).GetVtxoByScriptsAsSnapshot(
            Arg.Is<HashSet<string>>(x => x.SetEquals(new List<string>() { "script1" })),
            Arg.Any<CancellationToken>());
        arkClientTransport.Received(1).GetVtxoByScriptsAsSnapshot(
            Arg.Is<HashSet<string>>(x => x.SetEquals(new List<string>() { "script1", "script2" })),
            Arg.Any<CancellationToken>());
    }
}