using System.Runtime.CompilerServices;
using Ark.V1;

namespace NArk.Transport.GrpcClient;

public partial class GrpcClientTransport
{
    public async IAsyncEnumerable<ArkVtxo> GetVtxoSnapshotByScriptsAsync(IReadOnlySet<string> scripts, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var scriptsChunk in scripts.Chunk(1000))
        {
            var request = new GetVtxosRequest()
            {
                Scripts = { scriptsChunk },
                RecoverableOnly = false,
                SpendableOnly = false,
                SpentOnly = false,
                Page = new IndexerPageRequest()
                {
                    Index = 0,
                    Size = 1000
                }
            };
            
            GetVtxosResponse? response = null;

            while (response is null || response.Page.Next != response.Page.Total)
            {
                cancellationToken.ThrowIfCancellationRequested();
                response = await _indexerServiceClient.GetVtxosAsync(request, cancellationToken: cancellationToken);

                foreach (var vtxo in response.Vtxos)
                {
                    DateTimeOffset? expiresAt = null;
                    var maybeExpiresAt = DateTimeOffset.FromUnixTimeSeconds(vtxo.ExpiresAt);
                    if (maybeExpiresAt.Year >= 2025)
                        expiresAt = maybeExpiresAt;            

                    uint? expiresAtHeight = expiresAt.HasValue ? null : (uint)vtxo.ExpiresAt;
                    
                    yield return new ArkVtxo(
                        vtxo.Script,
                        vtxo.Outpoint.Txid,
                        vtxo.Outpoint.Vout,
                        vtxo.Amount,
                        vtxo.SpentBy,
                        vtxo.SettledBy,
                        vtxo is { IsSwept: true, IsSpent: false },
                        DateTimeOffset.FromUnixTimeSeconds(vtxo.CreatedAt), 
                        expiresAt,
                        expiresAtHeight
                    );
                }
                
                request.Page.Index = response.Page.Next;
            }
        }
    }
    
}