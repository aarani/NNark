using System.Net.Http.Json;
using System.Net.Sockets;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var bitcoin =
    builder
        .AddContainer("bitcoin", "ghcr.io/getumbrel/docker-bitcoind", "v29.0")
        .WithContainerName("bitcoin")
        .WithContainerNetworkAlias("bitcoin")
        .WithEndpoint(port: 18443, targetPort: 18443, protocol: ProtocolType.Tcp, name: "port")
        .WithEndpoint(port: 18444, targetPort: 18444, protocol: ProtocolType.Tcp, name: "rpcport")
        .WithEndpoint(28332, 28332, protocol: ProtocolType.Tcp, name: "zmqpub-block")
        .WithEndpoint(28333, 28333, protocol: ProtocolType.Tcp, name: "zmqpub-tx")
        .WithVolume("nark-bitcoind", target: "/data/.bitcoin")
        .WithLifetime(ContainerLifetime.Persistent)
        .WithContainerFiles("/data/.bitcoin/", "Assets/bitcoin.conf");

var electrs =
    builder
        .AddContainer("electrs", "ghcr.io/vulpemventures/electrs", "latest")
        .WithContainerName("electrs")
        .WithContainerNetworkAlias("electrs")
        .WithEntrypoint("/build/electrs")
        .WithEndpoint(50000, 50000, protocol: ProtocolType.Tcp, name: "rpc")
        .WithEndpoint(30000, 30000, protocol: ProtocolType.Tcp, name: "http")
        .WithArgs(
            "-vvvv",
            "--network", "regtest", "--daemon-dir", "/config",
            "--daemon-rpc-addr", "bitcoin:18443", "--cookie", "admin1:123",
            "--http-addr", "0.0.0.0:30000", "--electrum-rpc-addr", "0.0.0.0:50000", "--cors", "\"*\"", "--jsonrpc-import"
        )
        .WithVolume("nark-electrs", "/config")
        .WaitFor(bitcoin);

var chopsticks =
    builder
        .AddContainer("chopsticks", "ghcr.io/vulpemventures/nigiri-chopsticks", "latest")
        .WithContainerName("chopsticks")
        .WithContainerNetworkAlias("chopsticks")
        .WithArgs("--use-faucet", "--use-mining", "--use-logger", "--rpc-addr", "bitcoin:18443", "--electrs-addr",
            "electrs:30000", "--addr", "0.0.0.0:3000")
        .WithHttpEndpoint(3000, 3000, name: "http")
        .WaitFor(bitcoin)
        .WaitFor(electrs);

builder
    .AddContainer("esplora", "ghcr.io/vulpemventures/esplora", "latest")
    .WithContainerName("esplora")
    .WithContainerNetworkAlias("esplora")
    .WithEnvironment("API_URL", "http://localhost:3000")
    .WithEndpoint(5000, 5001, protocol: ProtocolType.Tcp, name: "http")
    .WaitFor(chopsticks);

var postgres =
    builder
        .AddPostgres("postgres")
        .WithContainerName("postgres")
        .WithContainerNetworkAlias("postgres")
        .WithHostPort(39372)
        .WithDataVolume("nark-postgres")
        .WithEnvironment("POSTGRES_HOST_AUTH_METHOD", "trust");

var arkdDb = postgres
    .AddDatabase("arkd-db", "arkd");

var nbxplorerDb = postgres
    .AddDatabase("nbxplorer-db", "nbxplorer");

var nbxplorer =
    builder
        .AddContainer("nbxplorer", "nicolasdorier/nbxplorer", "2.5.30-1")
        .WithContainerNetworkAlias("nbxplorer")
        .WithHttpEndpoint(32838, 32838, "http")
        .WithEnvironment("NBXPLORER_NETWORK", "regtest")
        .WithEnvironment("NBXPLORER_CHAINS", "btc")
        .WithEnvironment("NBXPLORER_BTCRPCURL", "http://bitcoin:18443/")
        .WithEnvironment("NBXPLORER_BTCNODEENDPOINT", "bitcoin:18444")
        .WithEnvironment("NBXPLORER_BTCRPCUSER", "admin1")
        .WithEnvironment("NBXPLORER_BTCRPCPASSWORD", "123")
        .WithEnvironment("NBXPLORER_VERBOSE", "1")
        .WithEnvironment("NBXPLORER_BIND", "0.0.0.0:32838")
        .WithEnvironment("NBXPLORER_TRIMEVENTS", "10000")
        .WithEnvironment("NBXPLORER_SIGNALFILESDIR", "/datadir")
        .WithEnvironment("NBXPLORER_POSTGRES",
            "User ID=postgres;Host=postgres;Port=5432;Application Name=nbxplorer;MaxPoolSize=20;Database=nbxplorer")
        .WithEnvironment("NBXPLORER_EXPOSERPC", "1")
        .WithEnvironment("NBXPLORER_NOAUTH", "1")
        .WithVolume("nark-nbxplorer", "/datadir")
        .WithHttpHealthCheck("/health", 200, "http")
        .WaitFor(nbxplorerDb)
        .WaitFor(bitcoin);

var arkWallet =
    builder
        .AddContainer("ark-wallet", "ghcr.io/arkade-os/arkd-wallet", "v0.8.10")
        .WithContainerName("ark-wallet")
        .WithContainerNetworkAlias("ark-wallet")
        .WaitFor(bitcoin)
        .WaitFor(nbxplorer)
        .WithEnvironment("ARKD_WALLET_LOG_LEVEL", "5")
        .WithEnvironment("ARKD_WALLET_NBXPLORER_URL", "http://nbxplorer:32838")
        .WithEnvironment("ARKD_WALLET_NETWORK", "regtest")
        .WithEnvironment("ARKD_WALLET_SIGNER_KEY", "19422b10efd05403820ff6a3365422be2fc5f07f34a6d1603f7298328f0f80f6")
        .WithVolume("nark-ark-wallet", "/app/data")
        .WithEndpoint(6060, 6060, protocol: ProtocolType.Tcp, name: "wallet");

var ark =
    builder
        .AddContainer("ark", "ghcr.io/arkade-os/arkd", "v0.8.10")
        .WithContainerName("ark")
        .WaitFor(bitcoin)
        .WaitFor(arkdDb)
        .WaitFor(arkWallet)
        .WithEnvironment("ARKD_LOG_LEVEL", "5")
        .WithEnvironment("ARKD_NO_TLS", "true")
        .WithEnvironment("ARKD_NO_MACAROONS", "true")
        .WithEnvironment("ARKD_WALLET_ADDR", "ark-wallet:6060")
        .WithEnvironment("ARKD_ESPLORA_URL", "http://chopsticks:3000")
        .WithEnvironment("ARKD_VTXO_MIN_AMOUNT", "1")
        .WithEnvironment("ARKD_VTXO_TREE_EXPIRY", "1024")
        .WithEnvironment("ARKD_UNILATERAL_EXIT_DELAY", "512")
        .WithEnvironment("ARKD_BOARDING_EXIT_DELAY", "2048")
        .WithEnvironment("ARKD_DB_TYPE", "sqlite")
        .WithEnvironment("ARKD_EVENT_DB_TYPE", "badger")
        .WithEnvironment("ARKD_LIVE_STORE_TYPE", "inmemory")
        .WithEnvironment("ARKD_UNLOCKER_TYPE", "env")
        .WithEnvironment("ARKD_UNLOCKER_PASSWORD", "secret")
        .WithVolume("nark-ark", "/app/data")
        .OnResourceReady(StartArkResource)
        .WithHttpEndpoint(7070, 7070, name: "arkd");

async Task StartArkResource(ContainerResource cr, ResourceReadyEvent @event, CancellationToken cancellationToken)
{
    var logger = @event.Services.GetRequiredService<ILogger<NArk_AppHost>>();

    var walletCreationProcess =
        await Cli.Wrap("docker")
            .WithArguments(["exec", "-t", "ark", "arkd", "wallet", "create", "--password", "secret"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

    if (!walletCreationProcess.IsSuccess &&
        !walletCreationProcess.StandardOutput.Contains("wallet already initialized") &&
        !walletCreationProcess.StandardError.Contains("wallet already initialized"))
    {
        logger.LogCritical(
            "Wallet creation failed, output = {stdOut}, error = {stdErr}",
            walletCreationProcess.StandardOutput,
            walletCreationProcess.StandardError
        );
    }

    var walletUnlockProcess =
        await Cli.Wrap("docker")
            .WithArguments(["exec", "-t", "ark", "arkd", "wallet", "unlock", "--password", "secret"])
            .ExecuteBufferedAsync(cancellationToken);

    if (!walletUnlockProcess.IsSuccess)
    {
        logger.LogCritical(
            "Wallet unlock failed, output = {stdOut}, error = {stdErr}",
            walletUnlockProcess.StandardOutput,
            walletUnlockProcess.StandardError
        );
    }

    int returnCode;
    do
    {
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        var walletStatus = await Cli.Wrap("docker")
            .WithArguments(["exec", "-t", "ark", "arkd", "wallet", "status"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);
        returnCode = walletStatus.ExitCode;
    } while (returnCode != 0);
    
    var arkInit =
        await Cli.Wrap("docker")
            .WithArguments(["exec", "-t", "ark", "ark", "init", "--password", "secret", "--server-url", "localhost:7070",
                "--explorer", "http://chopsticks:3000"])
            .ExecuteBufferedAsync(cancellationToken);

    if (!arkInit.IsSuccess)
    {
        logger.LogCritical(
            "Ark init failed, output = {stdOut}, error = {stdErr}",
            arkInit.StandardOutput,
            arkInit.StandardError
        );
    }
    
    var walletAddress =
        await Cli.Wrap("docker")
            .WithArguments(["exec", "-t", "ark", "arkd", "wallet", "address"])
            .ExecuteBufferedAsync(cancellationToken);
    
    var address = walletAddress.StandardOutput.Trim();
    var chopsticksEndpoint = await chopsticks.GetEndpoint("http", null).GetValueAsync(cancellationToken);
    await new HttpClient().PostAsJsonAsync($"{chopsticksEndpoint}/faucet", new
    {
        amount = 1,
        address = address
    }, cancellationToken: cancellationToken);

    var noteOutput = await Cli.Wrap("docker")
        .WithArguments(["exec", "-t", "ark", "arkd", "note", "--amount", "1000000"])
        .ExecuteBufferedAsync(cancellationToken);
    var note = noteOutput.StandardOutput.Trim();
    await Cli.Wrap("docker")
        .WithArguments(["exec", "-t", "ark", "ark", "redeem-notes", "-n", note, "--password", "secret"])
        .ExecuteBufferedAsync(cancellationToken);
}

builder
    .Build()
    .Run();