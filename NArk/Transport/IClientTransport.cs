using NArk.Abstractions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Transport.Models;

namespace NArk.Transport;

public interface IClientTransport
{
    Task<ArkServerInfo> GetServerInfoAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<HashSet<string>> GetVtxoToPollAsStream(IReadOnlySet<string> scripts, CancellationToken token = default);
    IAsyncEnumerable<ArkVtxo> GetVtxoByScriptsAsSnapshot(IReadOnlySet<string> scripts,
        CancellationToken cancellationToken = default);
    Task<string> RegisterIntent(ArkIntent intent, CancellationToken cancellationToken = default);
    Task DeleteIntent(ArkIntent intent, CancellationToken cancellationToken = default);
    Task<SubmitTxResponse> SubmitTx(string signedArkTx, string[] checkpointTxs, CancellationToken cancellationToken = default);
    Task FinalizeTx(string arkTxId, string[] finalCheckpointTxs, CancellationToken cancellationToken);
}