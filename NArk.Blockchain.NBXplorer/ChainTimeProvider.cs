using Microsoft.Extensions.Options;
using NArk.Abstractions.Blockchain;
using NBitcoin;
using NBXplorer;

namespace NArk.Blockchain.NBXplorer;

public class ChainTimeProvider : IChainTimeProvider
{
    private readonly ExplorerClient _client;

    public ChainTimeProvider(Network network, Uri uri)
    {
        _client = new ExplorerClient(new NBXplorerNetworkProvider(network.ChainName).GetBTC(), uri);
    }

    public ChainTimeProvider(IOptions<ChainTimeProviderOptions> options)
        : this(options.Value.Network, options.Value.Uri) { }

    public async Task<TimeHeight> GetChainTime(CancellationToken cancellationToken = default)
    {
        return new TimeHeight(
            DateTimeOffset.UtcNow,
            (uint)(await _client.GetStatusAsync(cancellationToken)).ChainHeight
        );
    }
}