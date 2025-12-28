using Aspire.Hosting;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Hosting;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Wallets;
using NArk.Blockchain.NBXplorer;
using NArk.Hosting;
using NArk.Models.Options;
using NArk.Services;
using NArk.Wallets;
using NBitcoin;

namespace NArk.Tests.End2End;

public class BuilderStyleTests
{
    private DistributedApplication _app;

    [OneTimeSetUp]
    public async Task StartDependencies()
    {
        ThreadPool.SetMinThreads(50, 50);

        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.NArk_AppHost>(
                args: ["--noswap"],
                configureBuilder: (appOptions, _) =>
                {
                    appOptions.AllowUnsecuredTransport = true;
                }
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
    public async Task CanParticipateInBatchSessionBuilderStyle()
    {
        var arkHost = ArkApplicationBuilder.CreateBuilder([])
            .OnCustomGrpcArk(_app.GetEndpoint("ark", "arkd").ToString())
            .WithIntentStorage<InMemoryIntentStorage>()
            .WithIntentScheduler<SimpleIntentScheduler>()
            .WithSwapStorage<InMemorySwapStorage>()
            .WithContractStorage<InMemoryContractStorage>()
            .WithVtxoStorage<InMemoryVtxoStorage>()
            .WithWalletStorage<InMemoryWalletStorage>()
            .WithWallet<SimpleSeedWallet>()
            .WithTimeProvider<ChainTimeProvider>()
            .ConfigureServices(s => s.Configure<ChainTimeProviderOptions>(o =>
            {
                o.Network = Network.RegTest;
                o.Uri = _app.GetEndpoint("nbxplorer", "http");
            }))
            .ConfigureServices(s => s.Configure<SimpleIntentSchedulerOptions>(o =>
            {
                o.Threshold = TimeSpan.FromHours(2);
                o.ThresholdHeight = 2000;
            }))
            .ConfigureServices(s => s.Configure<IntentGenerationServiceOptions>(o => o.PollInterval = TimeSpan.FromSeconds(5)))
            .Build();

        await arkHost.StartAsync();

        var contractService = arkHost.Services.GetRequiredService<IContractService>();
        var wallet = arkHost.Services.GetRequiredService<IWallet>();
        var intentStorage = arkHost.Services.GetRequiredService<IIntentStorage>();

        await wallet.CreateNewWallet("wallet1");
        var contract = await contractService.DerivePaymentContract("wallet1", CancellationToken.None);

        await Cli.Wrap("docker")
            .WithArguments([
                "exec", "-t", "ark", "ark", "send", "--to", contract.GetArkAddress().ToString(false), "--amount",
                "50000", "--password", "secret"
            ])
            .ExecuteBufferedAsync();

        var weGotAnswerCts = new CancellationTokenSource();

        intentStorage.IntentChanged += (sender, intent) =>
        {
            if (intent.State == ArkIntentState.BatchSucceeded)
                weGotAnswerCts.Cancel();
        };

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(5), weGotAnswerCts.Token);
        }
        catch (OperationCanceledException) when (weGotAnswerCts.IsCancellationRequested)
        {
            Assert.Pass();
        }

        Assert.Fail("We did not make a successful batch in the last 5 minute");

        await arkHost.StopAsync();
    }

}