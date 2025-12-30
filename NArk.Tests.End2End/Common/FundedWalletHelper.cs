using System.Security.Cryptography;
using Aspire.Hosting;
using CliWrap;
using CliWrap.Buffered;
using NArk.Contracts;
using NArk.Safety.AsyncKeyedLock;
using NArk.Services;
using NArk.Tests.End2End.TestPersistance;
using NArk.Transport;
using NArk.Transport.GrpcClient;
using NArk.Wallets;
using NSubstitute;

namespace NArk.Tests.End2End.Common;

internal static class FundedWalletHelper
{
    internal static async Task<(AsyncSafetyService safetyService, InMemoryWalletStorage inMemoryWalletStorage, InMemoryVtxoStorage vtxoStorage, ContractService contractService, InMemoryContractStorage contracts, SimpleSeedWallet wallet, IClientTransport clientTransport, VtxoSynchronizationService vtxoSync)> GetFundedWallet(DistributedApplication app)
    {
        var receivedFirstVtxoTcs = new TaskCompletionSource();
        var vtxoStorage = new InMemoryVtxoStorage();
        vtxoStorage.VtxosChanged += (sender, args) => receivedFirstVtxoTcs.TrySetResult();

        // Receive arkd information
        var clientTransport =
            Substitute.ForTypeForwardingTo<IClientTransport, GrpcClientTransport>(
                app.GetEndpoint("ark", "arkd").ToString()
            );

        var info = await clientTransport.GetServerInfoAsync();

        // Create a new wallet
        var inMemoryWalletStorage = new InMemoryWalletStorage();
        var contracts = new InMemoryContractStorage();
        var safetyService = new AsyncSafetyService();
        var wallet = new SimpleSeedWallet(safetyService, clientTransport, inMemoryWalletStorage);
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
        const int randomAmount = 500000;
        await Cli.Wrap("docker")
            .WithArguments([
                "exec", "-t", "ark", "ark", "send", "--to", contract.GetArkAddress().ToString(false), "--amount",
                randomAmount.ToString(), "--password", "secret"
            ])
            .ExecuteBufferedAsync();

        await receivedFirstVtxoTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        return (safetyService, inMemoryWalletStorage, vtxoStorage, contractService, contracts, wallet, clientTransport, vtxoSync);
    }
}