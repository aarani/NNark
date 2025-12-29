using Aspire.Hosting;
using BTCPayServer.Lightning;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Options;
using NArk.Services;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Models;
using NArk.Swaps.Services;
using NArk.Tests.End2End.Common;
using NArk.Tests.End2End.TestPersistance;
using NBitcoin;

namespace NArk.Tests.End2End;

public class SwapManagementServiceTests
{
    private DistributedApplication _app;

    [OneTimeSetUp]
    public async Task StartDependencies()
    {
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.NArk_AppHost>(
                args: [],
                configureBuilder: (appOptions, _) => { appOptions.AllowUnsecuredTransport = true; }
            );

        // Start dependencies
        _app = await builder.BuildAsync();
        await _app.StartAsync(CancellationToken.None);
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("boltz", CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(5)); //Boltz being boltz.... :(
    }

    [OneTimeTearDown]
    public async Task StopDependencies()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Test]

    public async Task CanPayInvoiceWithArkUsingBoltz()
    {
        var boltzApi = _app.GetEndpoint("boltz", "api");
        var boltzWs = _app.GetEndpoint("boltz", "ws");
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet(_app);
        var swapStorage = new InMemorySwapStorage();
        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
            { BoltzUrl = boltzApi.ToString(), WebsocketUrl = boltzWs.ToString() }));
        await using var swapMgr = new SwapsManagementService(
            new SpendingService(testingPrerequisite.vtxoStorage, testingPrerequisite.contracts,
                new SigningService(testingPrerequisite.wallet, testingPrerequisite.contracts,
                    testingPrerequisite.clientTransport),
                testingPrerequisite.contractService, testingPrerequisite.clientTransport),
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.wallet,
            swapStorage, testingPrerequisite.contractService, boltzClient);

        var settledSwapTcs = new TaskCompletionSource();

        swapStorage.SwapsChanged += (sender, swap) =>
        {
            if (swap.Status == ArkSwapStatus.Settled)
                settledSwapTcs.TrySetResult();
        };

        await swapMgr.StartAsync(CancellationToken.None);
        await swapMgr.InitiateSubmarineSwap(
            "wallet1",
            BOLT11PaymentRequest.Parse((await _app.ResourceCommands.ExecuteCommandAsync("lnd", "create-long-invoice"))
                .ErrorMessage!, Network.RegTest),
            true,
            CancellationToken.None
        );

        await settledSwapTcs.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

    [Test]
    [Ignore("This test requires sweeping logic, ignoring until that's implemented.")]

    public async Task CanReceiveArkFundsUsingReverseSwap()
    {
        var boltzApi = _app.GetEndpoint("boltz", "api");
        var boltzWs = _app.GetEndpoint("boltz", "ws");
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet(_app);
        var swapStorage = new InMemorySwapStorage();
        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
            { BoltzUrl = boltzApi.ToString(), WebsocketUrl = boltzWs.ToString() }));
        await using var swapMgr = new SwapsManagementService(
            new SpendingService(testingPrerequisite.vtxoStorage, testingPrerequisite.contracts,
                new SigningService(testingPrerequisite.wallet, testingPrerequisite.contracts,
                    testingPrerequisite.clientTransport),
                testingPrerequisite.contractService, testingPrerequisite.clientTransport),
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.wallet,
            swapStorage, testingPrerequisite.contractService, boltzClient);

        var settledSwapTcs = new TaskCompletionSource();

        swapStorage.SwapsChanged += (sender, swap) =>
        {
            if (swap.Status == ArkSwapStatus.Settled)
                settledSwapTcs.TrySetResult();
        };

        await swapMgr.StartAsync(CancellationToken.None);
        var invoice = await swapMgr.InitiateReverseSwap(
            "wallet1",
            new CreateInvoiceParams(LightMoney.Satoshis(50000), "Test", TimeSpan.FromHours(1)),
            CancellationToken.None
        );

        // Until Aspire has a way to run commands with parameters :(
        await Cli.Wrap("docker")
            .WithArguments(["exec", "lnd", "lncli", "--network=regtest", "payinvoice", "--force", invoice])
            .ExecuteBufferedAsync();

        await settledSwapTcs.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

    [Test]
    public async Task CanDoArkCoOpRefundUsingBoltz()
    {
        var boltzApi = _app.GetEndpoint("boltz", "api");
        var boltzWs = _app.GetEndpoint("boltz", "ws");
        var testingPrerequisite = await FundedWalletHelper.GetFundedWallet(_app);
        var swapStorage = new InMemorySwapStorage();
        var boltzClient = new BoltzClient(new HttpClient(),
            new OptionsWrapper<BoltzClientOptions>(new BoltzClientOptions()
            { BoltzUrl = boltzApi.ToString(), WebsocketUrl = boltzWs.ToString() }));
        await using var swapMgr = new SwapsManagementService(
            new SpendingService(testingPrerequisite.vtxoStorage, testingPrerequisite.contracts,
                new SigningService(testingPrerequisite.wallet, testingPrerequisite.contracts,
                    testingPrerequisite.clientTransport),
                testingPrerequisite.contractService, testingPrerequisite.clientTransport),
            testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
            testingPrerequisite.wallet,
            swapStorage, testingPrerequisite.contractService, boltzClient);

        var refundedSwapTcs = new TaskCompletionSource();

        swapStorage.SwapsChanged += (sender, swap) =>
        {
            if (swap.Status == ArkSwapStatus.Refunded)
                refundedSwapTcs.TrySetResult();
        };

        await swapMgr.StartAsync(CancellationToken.None);

        var invoice = (await _app.ResourceCommands.ExecuteCommandAsync("lnd", "create-invoice"))
            .ErrorMessage!;
        var swapId = await swapMgr.InitiateSubmarineSwap(
            "wallet1",
            BOLT11PaymentRequest.Parse(invoice, Network.RegTest),
            false,
            CancellationToken.None
        );

        // wait for invoice to expire
        await Task.Delay(TimeSpan.FromSeconds(30));

        await swapMgr.PayExistingSubmarineSwap("wallet1", swapId, CancellationToken.None);

        await refundedSwapTcs.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

}