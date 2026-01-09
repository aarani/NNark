using Ark.V1;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;

namespace NArk.Transport.GrpcClient;

public partial class GrpcClientTransport : IClientTransport
{
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(30);

    private readonly ArkService.ArkServiceClient _serviceClient;
    private readonly IndexerService.IndexerServiceClient _indexerServiceClient;

    public GrpcClientTransport(string uri) : this(uri, DefaultDeadline)
    {
    }

    public GrpcClientTransport(string uri, TimeSpan deadline)
    {
        var channel = GrpcChannel.ForAddress(uri);
        var invoker = channel.Intercept(new DeadlineInterceptor(deadline));

        _serviceClient = new ArkService.ArkServiceClient(invoker);
        _indexerServiceClient = new IndexerService.IndexerServiceClient(invoker);
    }
}