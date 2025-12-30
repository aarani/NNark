using System.Threading.Channels;
using Microsoft.Extensions.Options;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Contracts;
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
    IOptions<SweeperServiceOptions> options
) : IAsyncDisposable
{
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
        var multiToken = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, cancellationToken);
        _serverInfo = await clientTransport.GetServerInfoAsync(cancellationToken);
        _sweeperTask = DoSweepingLoop(multiToken.Token);
        vtxoStorage.VtxosChanged += OnVtxosChanged;
        contractStorage.ContractsChanged += OnContractsChanged;
        if (options.Value.ForceRefreshInterval != TimeSpan.Zero)
            _timer = new Timer(_ => _sweepJobTrigger.Writer.TryWrite(new SweepTimerTrigger()), null, TimeSpan.Zero,
                options.Value.ForceRefreshInterval);
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
            await contractStorage.LoadContractsByScripts(unspentVtxos.Select(x => x.Script).ToArray(), cancellationToken);
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
                    priorityQueue!.Enqueue(coin, priorityQueue.Count);
            }
        }

        await Sweep(outpointToCoins);
    }

    private async Task Sweep(Dictionary<OutPoint, PriorityQueue<ArkCoin, int>> outpointToCoins)
    {
        foreach (var (outpoint, possiblePaths) in outpointToCoins)
        {
            while (possiblePaths.Count > 0)
            {
                var possiblePath = possiblePaths.Dequeue();
                var signer = await wallet.FindSigningEntity(possiblePath.SignerDescriptor);
                var psbtSigner = new ArkPsbtSigner(possiblePath, signer);
                try
                {
                    await spendingService.Spend(possiblePath.WalletIdentifier, [psbtSigner], [],
                        CancellationToken.None);
                    break;
                }
                catch (Exception e)
                {
                    if (possiblePaths.Count == 0)
                    {
                        // log exception all paths failed.
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
        return new ArkUnspendableCoin(contract.WalletIdentifier, parsedContract, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
            vtxo.OutPoint, vtxo.TxOut, vtxo.Recoverable);
    }

    private void OnContractsChanged(object? sender, ArkContractEntity e) =>
        _sweepJobTrigger.Writer.TryWrite(new SweepContractTrigger([e]));

    private void OnVtxosChanged(object? sender, ArkVtxo e) =>
        _sweepJobTrigger.Writer.TryWrite(new SweepVtxoTrigger([e]));

    public async ValueTask DisposeAsync()
    {
        vtxoStorage.VtxosChanged -= OnVtxosChanged;
        contractStorage.ContractsChanged -= OnContractsChanged;

        try
        {
            if (_timer is not null)
                await _timer.DisposeAsync();
        }
        catch
        {
            // ignored
        }

        try
        {
            await _shutdownCts.CancelAsync();
        }
        catch
        {
            // ignored
        }

        try
        {
            if (_sweeperTask is not null)
                await _sweeperTask;
        }
        catch
        {
            // ignored
        }
    }
}