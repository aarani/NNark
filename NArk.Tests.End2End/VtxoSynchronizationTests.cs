using System.Security.Cryptography;
using Aspire.Hosting;
using CliWrap;
using CliWrap.Buffered;
using NArk.Abstractions;
using NArk.Abstractions.VTXOs;
using NArk.Contracts;
using NArk.Services;
using NArk.Transport.GrpcClient;
using NArk.Wallets;
using NBitcoin;
using NSubstitute;

namespace NArk.Tests.End2End;

public class VtxoSynchronizationTests
{
    private DistributedApplication _app;

    [OneTimeSetUp]
    public async Task StartDependencies()
    {
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.NArk_AppHost>(
                args: ["--noswap"],
                configureBuilder: (appOptions, _) => { appOptions.AllowUnsecuredTransport = true; }
            );

        // Start dependencies
        _app = await builder.BuildAsync();
        await _app.StartAsync(CancellationToken.None);
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("ark", CancellationToken.None);
    }

    [OneTimeTearDown]
    public async Task StopDependencies()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Test]
    public async Task CanReceiveVtxosFromImportedContract()
    {
        var network = Network.RegTest;

        // Mock the VTXO storage so we can listen for new vtxos
        var vtxoStorage = Substitute.For<IVtxoStorage>();
        vtxoStorage.GetUnspentVtxos().ReturnsForAnyArgs([]);
        vtxoStorage.SaveVtxo(Arg.Any<ArkVtxo>()).ReturnsForAnyArgs(Task.CompletedTask);

        // Receive arkd information
        var clientTransport = new GrpcClientTransport(_app.GetEndpoint("ark", "arkd").ToString());
        var info = await clientTransport.GetServerInfoAsync();

        // Create a new wallet
        var inMemoryWalletStorage = new InMemoryWalletStorage();
        var contracts = new InMemoryContractStorage();
        var wallet = new SimpleSeedWallet(clientTransport, inMemoryWalletStorage);
        await wallet.CreateNewWallet("wallet1");

        // Start vtxo synchronization service
        await using var vtxoSync = new VtxoSynchronizationService(
            inMemoryWalletStorage,
            vtxoStorage,
            contracts,
            clientTransport
        );
        await vtxoSync.StartAsync(CancellationToken.None);

        var contractService = new ContractService(wallet, contracts, clientTransport);

        // Generate a new payment contract, save to storage
        var signer = await wallet.GetNewSigningEntity("wallet1");
        var contract = new ArkPaymentContract(
            info.SignerKey,
            info.UnilateralExit,
            await signer.GetOutputDescriptor()
        );
        await contractService.ImportContract("wallet1", contract);

        // Pay a random amount to the contract address
        var randomAmount = RandomNumberGenerator.GetInt32((int)info.Dust.Satoshi, 100000);
        await Cli.Wrap("docker")
            .WithArguments([
                "exec", "-t", "ark", "ark", "send", "--to", contract.GetArkAddress().ToString(false), "--amount",
                randomAmount.ToString(), "--password", "secret"
            ])
            .ExecuteBufferedAsync();

        // Wait for the sync service to receive it
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Assert that we received the vtxo
        var vtxos = vtxoStorage.ReceivedCalls();
        Assert.That(
            vtxos
                .Any(v =>
                    v.GetMethodInfo().Name == nameof(IVtxoStorage.SaveVtxo) &&
                    ((ArkVtxo)v.GetArguments()[0]!).Script == contract.GetArkAddress().ScriptPubKey.ToHex() &&
                    ((ArkVtxo)v.GetArguments()[0]!).Amount == (ulong)randomAmount
                ),
            Is.True
        );
    }

    [Test]
    public async Task CanSendAndReceiveBackVtxo()
    {
        var network = Network.RegTest;

        // Receive arkd information
        var clientTransport = new GrpcClientTransport(_app.GetEndpoint("ark", "arkd").ToString());
        var info = await clientTransport.GetServerInfoAsync();

        // Create a new wallet
        var inMemoryWalletStorage = new InMemoryWalletStorage();
        var contracts = new InMemoryContractStorage();

        var vtxoStorage = GetMockVtxoStorageWithImpl();

        var wallet = new SimpleSeedWallet(clientTransport, inMemoryWalletStorage);
        await wallet.CreateNewWallet("wallet1");
        await wallet.CreateNewWallet("wallet2");

        var contractService = new ContractService(wallet, contracts, clientTransport);

        // Start vtxo synchronization service
        var vtxoSync = new VtxoSynchronizationService(
            inMemoryWalletStorage,
            vtxoStorage,
            contracts,
            clientTransport
        );
        await vtxoSync.StartAsync(CancellationToken.None);

        var contract = await contractService.DerivePaymentContract("wallet1");
        var wallet1Address = contract.GetArkAddress();

        // Pay a random amount to the contract address
        var randomAmount = RandomNumberGenerator.GetInt32((int)info.Dust.Satoshi, 100000);
        await Cli.Wrap("docker")
            .WithArguments([
                "exec", "-t", "ark", "ark", "send", "--to", wallet1Address.ToString(false),
                "--amount", randomAmount.ToString(), "--password", "secret"
            ])
            .ExecuteBufferedAsync();

        // Wait for the sync service to receive it
        await Task.Delay(TimeSpan.FromSeconds(15));

        // Assert that we received the vtxo
        var vtxos = vtxoStorage.ReceivedCalls();
        Assert.That(
            vtxos
                .Any(v =>
                    v.GetMethodInfo().Name == nameof(IVtxoStorage.SaveVtxo) &&
                    ((ArkVtxo)v.GetArguments()[0]!).Script == wallet1Address.ScriptPubKey.ToHex() &&
                    ((ArkVtxo)v.GetArguments()[0]!).Amount == (ulong)randomAmount
                ),
            Is.True
        );

        // Generate a new payment contract to receive funds from first wallet, save to storage
        var contract2 = await contractService.DerivePaymentContract("wallet2");
        var wallet2Address = contract2.GetArkAddress();

        vtxoStorage.ClearReceivedCalls();
        var spendingService = new SpendingService(vtxoStorage, contracts,
            new SigningService(wallet, contracts, clientTransport), contractService, clientTransport);

        await spendingService.Spend("wallet1",
        [
            new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(randomAmount), wallet2Address)
        ]);

        // Wait for the sync service to receive it
        await Task.Delay(TimeSpan.FromSeconds(10));

        // Flush the tasks
        await vtxoSync.DisposeAsync();

        // Assert that we received the vtxo
        var vtxos2 = vtxoStorage.ReceivedCalls();
        Assert.That(
            vtxos2
                .Any(v =>
                    v.GetMethodInfo().Name == nameof(IVtxoStorage.SaveVtxo) &&
                    ((ArkVtxo)v.GetArguments()[0]!).Script == wallet2Address.ScriptPubKey.ToHex() &&
                    ((ArkVtxo)v.GetArguments()[0]!).Amount == (ulong)randomAmount
                ),
            Is.True
        );
    }

    private static IVtxoStorage GetMockVtxoStorageWithImpl()
    {
        // Setup VTXO Storage
        var vtxoStorageImpl = new InMemoryVtxoStorage();
        var vtxoStorage = Substitute.For<IVtxoStorage>();

        vtxoStorage.GetAllVtxos().ReturnsForAnyArgs((_) => vtxoStorageImpl.GetAllVtxos());
        vtxoStorage.GetUnspentVtxos().ReturnsForAnyArgs((_) => vtxoStorageImpl.GetUnspentVtxos());
        vtxoStorage.GetVtxosByScripts(
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<bool>()
            )
            .ReturnsForAnyArgs(c =>
                vtxoStorageImpl.GetVtxosByScripts(c.Arg<IReadOnlyCollection<string>>(), c.Arg<bool>()));
        vtxoStorage.GetVtxoByOutPoint(Arg.Any<OutPoint>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(c => vtxoStorageImpl.GetVtxoByOutPoint(c.Arg<OutPoint>(), c.Arg<CancellationToken>()));
        vtxoStorage.SaveVtxo(Arg.Any<ArkVtxo>()).ReturnsForAnyArgs(c => vtxoStorageImpl.SaveVtxo(c.Arg<ArkVtxo>()));

        return vtxoStorage;
    }
}