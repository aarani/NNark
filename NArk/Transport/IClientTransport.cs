using NArk.Abstractions;

namespace NArk.Transport;

public interface IClientTransport
{
    Task<ArkServerInfo> GetServerInfoAsync(CancellationToken cancellationToken = default);
}