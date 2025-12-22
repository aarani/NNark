using CliWrap;
using CliWrap.Buffered;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Contracts;
using NArk.Extensions;
using NArk.Services;
using NArk.Transport.GrpcClient;
using NArk.Wallets;
using NBitcoin;
using NSubstitute;

namespace NArk.Tests.End2End;

public class PaymentSupport
{
    [Test]
    public async Task CanSendPaymentsToArkWallet()
    {
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.NArk_AppHost>(
                args: [],
                configureBuilder: (appOptions, hostSettings) =>
                {
                    appOptions.DisableDashboard = false;
                    appOptions.AllowUnsecuredTransport = true;
                });

        var a = await builder.BuildAsync();
        await a.RunAsync();
        await a.ResourceNotifications.WaitForResourceHealthyAsync("ark", CancellationToken.None);
        
        var network = Network.RegTest;
        var clientTransport = new GrpcClientTransport("http://localhost:7070");
        var info = await clientTransport.GetServerInfoAsync();
        var inMemoryWalletStorage = new InMemoryWalletStorage();
        var wallet = new SimpleSeedWallet(network, inMemoryWalletStorage);
        await wallet.CreateNewWallet("wallet1");
        var signer = await wallet.GetNewSigningEntity("wallet1");
        var contract = new ArkPaymentContract(
            KeyExtensions.ParseOutputDescriptor(Convert.ToHexStringLower(info.SignerKey.ToBytes()), network),
            info.UnilateralExit,
            await signer.GetOutputDescriptor()
        );
        var contracts = new InMemoryContractStorage();
        var arkContractEntity = contract.ToEntity("wallet1");
        await contracts.SaveContract("wallet1", arkContractEntity);
        var address = contract.GetArkAddress().ToString(false);
        var vtxoStorage = NSubstitute.Substitute.For<IVtxoStorage>();
        vtxoStorage.GetUnspentVtxos().ReturnsForAnyArgs([]);
        await using var vtxoSync = new VtxoSynchronizationService(
            inMemoryWalletStorage,
            vtxoStorage,
            contracts,
            clientTransport
        );
        await vtxoSync.Start();
        await Cli.Wrap("docker")
            .WithArguments([
                "exec", "-t", "ark", "ark", "send", "--to", address, "--amount", "100", "--password", "secret"
            ])
            .ExecuteBufferedAsync();
        await Task.Delay(TimeSpan.FromSeconds(10));
        var vtxos = vtxoStorage.SaveVtxo(Arg.Any<ArkVtxo>()).ReceivedCalls();
        Assert.That(vtxos.Any(v => ((ArkVtxo)v.GetArguments()[0]!).Script == arkContractEntity.Script));
    }
}