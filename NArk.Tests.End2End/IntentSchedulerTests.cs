using System.Security.Cryptography;
using Aspire.Hosting;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Intents;
using NArk.Blockchain.NBXplorer;
using NArk.Contracts;
using NArk.Models.Options;
using NArk.Services;
using NArk.Transport;
using NArk.Transport.GrpcClient;
using NArk.Wallets;
using NBitcoin;
using NSubstitute;

namespace NArk.Tests.End2End;

public class IntentSchedulerTests
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

    private async Task<(InMemoryWalletStorage inMemoryWalletStorage, InMemoryVtxoStorage vtxoStorage, ContractService
        contractService, InMemoryContractStorage contracts, SimpleSeedWallet wallet, IClientTransport clientTransport,
        VtxoSynchronizationService vtxoSync)> GetFundedWallet()
    {
        var network = Network.RegTest;

        var vtxoStorage = new InMemoryVtxoStorage();

        // Receive arkd information
        var clientTransport =
            Substitute.ForTypeForwardingTo<IClientTransport, GrpcClientTransport>(_app.GetEndpoint("ark", "arkd")
                .ToString());

        var info = await clientTransport.GetServerInfoAsync();

        // Create a new wallet
        var inMemoryWalletStorage = new InMemoryWalletStorage();
        var contracts = new InMemoryContractStorage();
        var wallet = new SimpleSeedWallet(clientTransport, inMemoryWalletStorage);
        await wallet.CreateNewWallet("wallet1");

        // Start vtxo synchronization service
        var vtxoSync = new VtxoSynchronizationService(
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

        return (inMemoryWalletStorage, vtxoStorage, contractService, contracts, wallet, clientTransport, vtxoSync);
    }

    [Test]
    public async Task CanScheduleIntent()
    {
        var walletDetails = await GetFundedWallet();

        // The threshold is so high, it will force an intent generation
        var scheduler = new SimpleIntentScheduler(walletDetails.contractService,
            new ChainTimeProvider(Network.RegTest, _app.GetEndpoint("nbxplorer", "http")),
            new OptionsWrapper<SimpleIntentSchedulerOptions>(new SimpleIntentSchedulerOptions()
            { Threshold = TimeSpan.FromHours(2), ThresholdHeight = 2000 }));
        var intentStorage = new InMemoryIntentStorage();
        await using (var intentGeneration = new IntentGenerationService(walletDetails.clientTransport,
                         walletDetails.inMemoryWalletStorage,
                         new SigningService(walletDetails.wallet, walletDetails.contracts,
                             walletDetails.clientTransport), intentStorage,
                         walletDetails.contracts, walletDetails.vtxoStorage, scheduler,
                         new OptionsWrapper<IntentGenerationServiceOptions>(new IntentGenerationServiceOptions()
                         { PollInterval = TimeSpan.FromMinutes(5) })))
        {
            await intentGeneration.StartAsync();
            await Task.Delay(TimeSpan.FromSeconds(15));
        }

        await using (var intentSync = new IntentSynchronizationService(intentStorage, walletDetails.clientTransport))
        {
            await intentSync.StartAsync();
            await Task.Delay(TimeSpan.FromSeconds(15));
        }

        Assert.That((await intentStorage.GetIntents("wallet1")).Any(i => i.State is ArkIntentState.WaitingForBatch),
            Is.True);
    }
}