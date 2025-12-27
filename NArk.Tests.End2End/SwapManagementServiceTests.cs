using System.Security.Cryptography;
using Aspire.Hosting;
using BTCPayServer.Lightning;
using CliWrap;
using CliWrap.Buffered;
using NArk.Contracts;
using NArk.Services;
using NArk.Swaps.Models;
using NArk.Swaps.Services;
using NArk.Transport;
using NArk.Transport.GrpcClient;
using NArk.Wallets;
using NBitcoin;
using NSubstitute;

namespace NArk.Tests.End2End;

public class SwapManagementServiceTests
{
    private async Task<(InMemoryWalletStorage inMemoryWalletStorage, InMemoryVtxoStorage vtxoStorage, ContractService contractService, InMemoryContractStorage contracts, SimpleSeedWallet wallet, IClientTransport clientTransport, VtxoSynchronizationService vtxoSync)> GetFundedWallet()
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
        var wallet = new SimpleSeedWallet(network, inMemoryWalletStorage);
        await wallet.CreateNewWallet("wallet1");

        // Start vtxo synchronization service
        var vtxoSync = new VtxoSynchronizationService(
            inMemoryWalletStorage,
            vtxoStorage,
            contracts,
            clientTransport
        );
        await vtxoSync.Start();

        var contractService = new ContractService(wallet, contracts, clientTransport);

        // Generate a new payment contract, save to storage
        var signer = await wallet.GetNewSigningEntity("wallet1");
        var contract = new ArkPaymentContract(
            info.SignerKey,
            info.UnilateralExit,
            await signer.GetOutputDescriptor()
        );
        await contractService.ImportContract("wallet1", contract);

        await Cli.Wrap("docker")
            .WithArguments([
                "exec", "-t", "ark", "ark", "send", "--to", contract.GetArkAddress().ToString(false), "--amount",
                "500000", "--password", "secret"
            ])
            .ExecuteBufferedAsync();

        // Wait for the sync service to receive it
        await Task.Delay(TimeSpan.FromSeconds(5));

        return (inMemoryWalletStorage, vtxoStorage, contractService, contracts, wallet, clientTransport, vtxoSync);
    }


    private DistributedApplication _app;

    [SetUp]
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
    }

    [TearDown]
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
        var testingPrerequisite = await GetFundedWallet();
        var swapStorage = new InMemorySwapStorage();
        await using (var swapMgr = new SwapsManagementService(
                         boltzApi,
                         boltzWs,
                         new SpendingService(testingPrerequisite.vtxoStorage, testingPrerequisite.contracts,
                             new SigningService(testingPrerequisite.wallet, testingPrerequisite.contracts,
                                 Network.RegTest),
                             testingPrerequisite.contractService, testingPrerequisite.clientTransport),
                         testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
                         testingPrerequisite.wallet,
                         swapStorage, testingPrerequisite.contractService))
        {
            await swapMgr.StartAsync(CancellationToken.None);
            await swapMgr.InitiateSubmarineSwap(
                "wallet1",
                BOLT11PaymentRequest.Parse((await _app.ResourceCommands.ExecuteCommandAsync("lnd", "create-invoice"))
                    .ErrorMessage!, Network.RegTest),
                true,
                CancellationToken.None
            );

            await Task.Delay(TimeSpan.FromSeconds(20));
        }
        Assert.That(
            (await swapStorage.GetActiveSwaps("wallet1")).Any(s => s.Status == ArkSwapStatus.Settled),
            Is.True
        );
    }

    [Test]
    public async Task CanDoArkCoOpRefundUsingBoltz()
    {
        var boltzApi = _app.GetEndpoint("boltz", "api");
        var boltzWs = _app.GetEndpoint("boltz", "ws");
        var testingPrerequisite = await GetFundedWallet();
        var swapStorage = new InMemorySwapStorage();
        await using (var swapMgr = new SwapsManagementService(
             boltzApi,
             boltzWs,
             new SpendingService(testingPrerequisite.vtxoStorage, testingPrerequisite.contracts,
                 new SigningService(testingPrerequisite.wallet, testingPrerequisite.contracts,
                     Network.RegTest),
                 testingPrerequisite.contractService, testingPrerequisite.clientTransport),
             testingPrerequisite.clientTransport, testingPrerequisite.vtxoStorage,
             testingPrerequisite.wallet,
             swapStorage, testingPrerequisite.contractService))
        {
            await swapMgr.StartAsync(CancellationToken.None);
            var invoice = (await _app.ResourceCommands.ExecuteCommandAsync("lnd", "create-invoice"))
                .ErrorMessage!;
            var swapId = await swapMgr.InitiateSubmarineSwap(
                "wallet1",
                BOLT11PaymentRequest.Parse(invoice, Network.RegTest),
                false,
                CancellationToken.None
            );
            await Task.Delay(TimeSpan.FromSeconds(30));
            await swapMgr.PayExistingSubmarineSwap("wallet1", swapId, CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(45));
        }
        Assert.That(
            (await swapStorage.GetActiveSwaps("wallet1")).Any(s => s.Status == ArkSwapStatus.Refunded),
            Is.True
        );
    }

}