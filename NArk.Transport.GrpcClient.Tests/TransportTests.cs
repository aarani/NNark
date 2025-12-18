namespace NArk.Transport.GrpcClient.Tests;

public class TransportTests
{
    [Test]
    public void CanConnecToMainnetArk()
    {
        var transport = new GrpcClientTransport("https://arkade.computer");
        Assert.DoesNotThrowAsync(async () => await transport.GetServerInfoAsync());
    }
}