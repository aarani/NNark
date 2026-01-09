using System.Security.Cryptography;
using Aspire.Hosting;
using CliWrap;
using CliWrap.Buffered;
using NArk.Abstractions;
using NArk.Contracts;
using NArk.Safety.AsyncKeyedLock;
using NArk.Services;
using NArk.Tests.End2End.TestPersistance;
using NArk.Transformers;
using NArk.Transport.GrpcClient;
using NArk.Wallets;
using NBitcoin;
using DefaultCoinSelector = NArk.CoinSelector.DefaultCoinSelector;

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
        // Receive arkd information
        var clientTransport = new GrpcClientTransport(_app.GetEndpoint("ark", "arkd").ToString());
        var info = await clientTransport.GetServerInfoAsync();

        // Pay a random amount to the contract address
        var randomAmount = RandomNumberGenerator.GetInt32((int)info.Dust.Satoshi, 100000);

        // Listen for incoming vtxos
        var receiveTcs = new TaskCompletionSource();
        var vtxoStorage = new InMemoryVtxoStorage();

        vtxoStorage.VtxosChanged += (_, vtxo) =>
        {
            if (!vtxo.IsSpent() && vtxo.Amount == (ulong)randomAmount)
            {
                receiveTcs.TrySetResult();
            }
        };

        // Create a new wallet
        var inMemoryWalletStorage = new InMemoryWalletStorage();
        var inMemoryKeyStorage = new InMemoryKeyStorage();
        var contracts = new InMemoryContractStorage();
        var safetyService = new AsyncSafetyService();
        var wallet = new SimpleSeedWallet(safetyService, clientTransport, inMemoryWalletStorage, inMemoryKeyStorage);
        await wallet.CreateNewWallet("wallet1");

        // Start vtxo synchronization service
        await using var vtxoSync = new VtxoSynchronizationService(
            vtxoStorage,
            contracts,
            clientTransport
        );
        await vtxoSync.StartAsync(CancellationToken.None);

        var contractService = new ContractService(wallet, contracts, clientTransport);

        // Generate a new payment contract, save to storage
        var signer = await wallet.DeriveNextDescriptor("wallet1");
        var contract = new ArkPaymentContract(
            info.SignerKey,
            info.UnilateralExit,
            signer
        );
        await contractService.ImportContract("wallet1", contract);

        await Cli.Wrap("docker")
            .WithArguments([
                "exec", "-t", "ark", "ark", "send", "--to", contract.GetArkAddress().ToString(false), "--amount",
                randomAmount.ToString(), "--password", "secret"
            ])
            .ExecuteBufferedAsync();

        // Wait for the sync service to receive it
        await receiveTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Test]
    public async Task CanSendAndReceiveBackVtxo()
    {
        // Receive arkd information
        var clientTransport = new GrpcClientTransport(_app.GetEndpoint("ark", "arkd").ToString());

        // Create a new wallet
        var inMemoryWalletStorage = new InMemoryWalletStorage();
        var inMemoryKeyStorage = new InMemoryKeyStorage();
        var contracts = new InMemoryContractStorage();

        var vtxoStorage = new InMemoryVtxoStorage();

        var safetyService = new AsyncSafetyService();

        var wallet = new SimpleSeedWallet(safetyService, clientTransport, inMemoryWalletStorage, inMemoryKeyStorage);
        await wallet.CreateNewWallet("wallet1");
        await wallet.CreateNewWallet("wallet2");

        var contractService = new ContractService(wallet, contracts, clientTransport);

        // Start vtxo synchronization service
        await using var vtxoSync = new VtxoSynchronizationService(
            vtxoStorage,
            contracts,
            clientTransport
        );
        await vtxoSync.StartAsync(CancellationToken.None);

        var contract = await contractService.DerivePaymentContract("wallet1");
        var wallet1Address = contract.GetArkAddress();

        // Pay a random amount to the contract address
        var randomAmount = 50000;
        var receiveTcs = new TaskCompletionSource();
        var receiveHalfTcs = new TaskCompletionSource();

        vtxoStorage.VtxosChanged += (_, vtxo) =>
        {
            if (!vtxo.IsSpent() && (ulong)randomAmount == vtxo.Amount)
            {
                receiveTcs.TrySetResult();
            }
            else if (!vtxo.IsSpent() && (ulong)(randomAmount / 2) == vtxo.Amount)
            {
                receiveHalfTcs.TrySetResult();
            }
        };

        await Cli.Wrap("docker")
            .WithArguments([
                "exec", "-t", "ark", "ark", "send", "--to", wallet1Address.ToString(false),
                "--amount", randomAmount.ToString(), "--password", "secret"
            ])
            .ExecuteBufferedAsync();

        // Wait for the sync service to receive it
        await receiveTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // Generate a new payment contract to receive funds from first wallet, save to storage
        var contract2 = await contractService.DerivePaymentContract("wallet2");
        var wallet2Address = contract2.GetArkAddress();

        
        var coinService = new CoinService(clientTransport, contracts,
            [new PaymentContractTransformer(), new HashLockedContractTransformer()]);

        var spendingService = new SpendingService(vtxoStorage, contracts,
            new SigningService(inMemoryKeyStorage), coinService, contractService, clientTransport, new DefaultCoinSelector(), safetyService, new InMemoryIntentStorage());

        await spendingService.Spend("wallet1",
        [
            new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(randomAmount / 2), wallet2Address)
        ]);

        await receiveHalfTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}