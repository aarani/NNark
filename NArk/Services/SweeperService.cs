using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Contracts;
using NArk.Enums;
using NArk.Events;
using NArk.Extensions;
using NArk.Models.Options;
using NArk.Sweeper;
using NArk.Transactions;
using NArk.Transport;
using NBitcoin;

namespace NArk.Services;

public class SweeperService(
    IWallet wallet,
    IClientTransport clientTransport,
    IEnumerable<ISweepPolicy> policies,
    IVtxoStorage vtxoStorage,
    IContractStorage contractStorage,
    ISpendingService spendingService,
    IOptions<SweeperServiceOptions> options,
    IEnumerable<IEventHandler<PostSweepActionEvent>> postSweepHandlers,
    ILogger<SweeperService>? logger = null) : IAsyncDisposable
{
    public SweeperService(
        IWallet wallet,
        IClientTransport clientTransport,
        IEnumerable<ISweepPolicy> policies,
        IVtxoStorage vtxoStorage,
        IContractStorage contractStorage,
        ISpendingService spendingService,
        IOptions<SweeperServiceOptions> options)
            : this(wallet, clientTransport, policies, vtxoStorage, contractStorage, spendingService, options, [], null)
    {
    }

    public SweeperService(
        IWallet wallet,
        IClientTransport clientTransport,
        IEnumerable<ISweepPolicy> policies,
        IVtxoStorage vtxoStorage,
        IContractStorage contractStorage,
        ISpendingService spendingService,
        IOptions<SweeperServiceOptions> options,
        ILogger<SweeperService> logger)
            : this(wallet, clientTransport, policies, vtxoStorage, contractStorage, spendingService, options, [], logger)
    {
    }

    private record SweepJobTrigger;
    private record SweepTimerTrigger : SweepJobTrigger;
    private record SweepVtxoTrigger(IReadOnlyCollection<ArkVtxo> Vtxos) : SweepJobTrigger;
    private record SweepContractTrigger(IReadOnlyCollection<ArkContractEntity> Contracts) : SweepJobTrigger;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Channel<SweepJobTrigger> _sweepJobTrigger = Channel.CreateUnbounded<SweepJobTrigger>();

    private ArkServerInfo? _serverInfo;
    private Task? _sweeperTask;
    private Timer? _timer;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger?.LogInformation("Starting sweeper service");
        var multiToken = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, cancellationToken);
        _serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        _sweeperTask = DoSweepingLoop(multiToken.Token);
        vtxoStorage.VtxosChanged += OnVtxosChanged;
        contractStorage.ContractsChanged += OnContractsChanged;
        if (options.Value.ForceRefreshInterval != TimeSpan.Zero)
            _timer = new Timer(_ => _sweepJobTrigger.Writer.TryWrite(new SweepTimerTrigger()), null, TimeSpan.Zero,
                options.Value.ForceRefreshInterval);
        logger?.LogDebug("Sweeper service started with refresh interval {Interval}", options.Value.ForceRefreshInterval);
    }

    private async Task DoSweepingLoop(CancellationToken loopShutdownToken)
    {
        await foreach (var reason in _sweepJobTrigger.Reader.ReadAllAsync(loopShutdownToken))
        {
            await (reason switch
            {
                SweepVtxoTrigger vtxoTrigger => TrySweepVtxos(vtxoTrigger.Vtxos, loopShutdownToken),
                SweepContractTrigger contractTrigger => TrySweepContracts(contractTrigger.Contracts, loopShutdownToken),
                SweepTimerTrigger _ => TrySweepContracts([], loopShutdownToken),
                _ => throw new ArgumentOutOfRangeException()
            });
        }
    }

    private async Task TrySweepContracts(IReadOnlyCollection<ArkContractEntity> contracts,
        CancellationToken cancellationToken)
    {
        if (contracts.Count is 0)
            contracts = await contractStorage.LoadActiveContracts(cancellationToken: cancellationToken);
        var matchingVtxos =
            await vtxoStorage.GetVtxosByScripts(contracts.Select(c => c.Script).ToArray(), false, cancellationToken);
        var coins = matchingVtxos.Join(contracts, v => v.Script, c => c.Script,
                (vtxo, entity) => GetUnspendableCoin(vtxo, entity, _serverInfo!),
                StringComparer.InvariantCultureIgnoreCase)
            .ToArray();
        await ExecutePoliciesAsync(coins);
    }

    private async Task TrySweepVtxos(IReadOnlyCollection<ArkVtxo> vtxos, CancellationToken cancellationToken)
    {
        var unspentVtxos = vtxos.Where(v => !v.IsSpent()).ToArray();
        var matchingContracts =
            await contractStorage.LoadContractsByScripts(unspentVtxos.Select(x => x.Script).ToArray(),
                cancellationToken);
        var coins =
            unspentVtxos
                .Join(matchingContracts, v => v.Script, c => c.Script,
                    (vtxo, entity) => GetUnspendableCoin(vtxo, entity, _serverInfo!),
                    StringComparer.InvariantCultureIgnoreCase
                )
                .ToArray();
        await ExecutePoliciesAsync(coins);
    }

    private async Task ExecutePoliciesAsync(IReadOnlyCollection<ArkUnspendableCoin> coins)
    {
        Dictionary<OutPoint, PriorityQueue<ArkCoin, int>> outpointToCoins = new();

        foreach (var policy in policies)
        {
            if (!policy.CanSweep(coins)) continue;

            await foreach (var coin in policy.SweepAsync(coins))
            {
                if (!outpointToCoins.TryGetValue(coin.Outpoint, out var priorityQueue))
                    outpointToCoins[coin.Outpoint] = new PriorityQueue<ArkCoin, int>([(coin, 1)]);
                else
                    priorityQueue.Enqueue(coin, priorityQueue.Count);
            }
        }

        await Sweep(outpointToCoins);
    }

    private async Task Sweep(Dictionary<OutPoint, PriorityQueue<ArkCoin, int>> outpointToCoins)
    {
        logger?.LogDebug("Starting sweep for {OutpointCount} outpoints", outpointToCoins.Count);
        foreach (var (outpoint, possiblePaths) in outpointToCoins)
        {
            while (possiblePaths.Count > 0)
            {
                var possiblePath = possiblePaths.Dequeue();
                var signer = await wallet.FindSigningEntity(possiblePath.SignerDescriptor);
                var psbtSigner = new ArkPsbtSigner(possiblePath, signer);
                try
                {
                    var txId = await spendingService.Spend(possiblePath.WalletIdentifier, [psbtSigner], [],
                        CancellationToken.None);
                    logger?.LogInformation("Sweep successful for outpoint {Outpoint}, txId: {TxId}", outpoint, txId);
                    await postSweepHandlers.SafeHandleEventAsync(new PostSweepActionEvent(psbtSigner.Coin, txId,
                        ActionState.Successful, null));
                    break;
                }
                catch (AlreadyLockedVtxoException ex)
                {
                    logger?.LogWarning(0, ex, "Sweep skipped for outpoint {Outpoint}: vtxo is already locked", outpoint);
                    await postSweepHandlers.SafeHandleEventAsync(new PostSweepActionEvent(psbtSigner.Coin, null,
                        ActionState.Failed, "Vtxo is already locked by another process."));
                    break;
                }
                catch (Exception ex)
                {
                    if (possiblePaths.Count == 0)
                    {
                        logger?.LogError(0, ex, "All sweep paths failed for outpoint {Outpoint}", outpoint);
                        await postSweepHandlers.SafeHandleEventAsync(new PostSweepActionEvent(psbtSigner.Coin, null,
                            ActionState.Failed, $"All sweeping paths failed, ex: {ex}"));
                    }
                    else
                    {
                        logger?.LogDebug(0, ex, "Sweep path failed for outpoint {Outpoint}, trying next path ({RemainingPaths} remaining)", outpoint, possiblePaths.Count);
                    }
                }
            }
        }
    }

    private static ArkUnspendableCoin GetUnspendableCoin(ArkVtxo vtxo, ArkContractEntity contract,
        ArkServerInfo serverInfo)
    {
        var parsedContract = ArkContract.Parse(contract.Type, contract.AdditionalData, serverInfo.Network);
        if (parsedContract is null)
            throw new UnableToSignUnknownContracts(
                $"Could not parse contract belonging to vtxo {vtxo.TransactionId}:{vtxo.TransactionOutputIndex}");
        return new ArkUnspendableCoin(contract.WalletIdentifier, parsedContract, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
            vtxo.OutPoint, vtxo.TxOut, vtxo.Recoverable);
    }

    private void OnContractsChanged(object? sender, ArkContractEntity e) =>
        _sweepJobTrigger.Writer.TryWrite(new SweepContractTrigger([e]));

    private void OnVtxosChanged(object? sender, ArkVtxo e) =>
        _sweepJobTrigger.Writer.TryWrite(new SweepVtxoTrigger([e]));

    public async ValueTask DisposeAsync()
    {
        logger?.LogDebug("Disposing sweeper service");
        vtxoStorage.VtxosChanged -= OnVtxosChanged;
        contractStorage.ContractsChanged -= OnContractsChanged;

        try
        {
            if (_timer is not null)
                await _timer.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger?.LogDebug(0, ex, "Error disposing timer during sweeper service shutdown");
        }

        try
        {
            await _shutdownCts.CancelAsync();
        }
        catch (Exception ex)
        {
            logger?.LogDebug(0, ex, "Error cancelling shutdown token during sweeper service shutdown");
        }

        try
        {
            if (_sweeperTask is not null)
                await _sweeperTask;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(0, ex, "Sweeper task completed with error during shutdown");
        }

        logger?.LogInformation("Sweeper service disposed");
    }
}