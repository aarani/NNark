using Ark.V1;
using NArk.Abstractions.Intents;

namespace NArk.Transport.GrpcClient;

public partial class GrpcClientTransport
{
    public async Task<string> RegisterIntent(ArkIntent intent, CancellationToken cancellationToken = default)
    {
        try
        {
            var registerRequest = new RegisterIntentRequest
            {
                Intent = new Intent()
                {
                    Message = intent.RegisterProofMessage,
                    Proof = intent.RegisterProof
                }
            };

            var response = await _serviceClient.RegisterIntentAsync(registerRequest, cancellationToken: cancellationToken);

            return response.IntentId;
        }
        catch (Exception ex) when (ex.Message.Contains("duplicated input"))
        {
            throw new AlreadyLockedVtxoException();
        }
    }
}