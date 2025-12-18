using Ark.V1;
using Grpc.Net.Client;
using NArk.Abstractions;
using NArk.Scripts;
using NArk.Transport.GrpcClient.Extensions;
using NBitcoin;
namespace NArk.Transport.GrpcClient;

public class GrpcClientTransport: IClientTransport
{
    private readonly ArkService.ArkServiceClient _client;

    public GrpcClientTransport(string uri)
    {
        var channel = GrpcChannel.ForAddress(uri);
        _client = new Ark.V1.ArkService.ArkServiceClient(channel);
    }
    
    private static Sequence ParseSequence(long val)
    {
        return val >= 512 ? new Sequence(TimeSpan.FromSeconds(val)) : new Sequence((int)val);
    }
    
    public async Task<ArkServerInfo> GetServerInfoAsync(CancellationToken cancellationToken = default)
    {
        var response = await _client.GetInfoAsync(new GetInfoRequest() , cancellationToken: cancellationToken);
        var network =
            response.Network switch
            {
                _ when Network.GetNetwork(response.Network) is { } net => net,
                "bitcoin" => Network.Main,
                _ => throw new InvalidOperationException("Ark server advertises unknown network")
            };

        var serverUnrollScript = UnilateralPathArkTapScript.Parse(response.CheckpointTapscript);
        
        if (ParseSequence(response.UnilateralExitDelay) != serverUnrollScript.Timeout)
            throw new InvalidOperationException("Ark server advertises inconsistent unilateral exit delay");

        var fPubKey = response.ForfeitPubkey.ToECXOnlyPubKey();
        
        if (!serverUnrollScript.OwnersMultiSig.Owners[0].ToBytes().SequenceEqual(fPubKey.ToBytes()))
            throw new InvalidOperationException("Ark server advertises inconsistent forfeit pubkey");
        
        return new ArkServerInfo(
            Dust: Money.Satoshis(response.Dust),
            SignerKey: response.SignerPubkey.ToECXOnlyPubKey(),
            DeprecatedSigners: response.DeprecatedSigners.ToDictionary(signer => signer.Pubkey.ToECXOnlyPubKey(),
                signer => signer.CutoffDate),
            Network: network,
            UnilateralExit: ParseSequence(response.UnilateralExitDelay),
            BoardingExit: ParseSequence(response.BoardingExitDelay),
            ForfeitAddress: BitcoinAddress.Create(response.ForfeitAddress, network),
            ForfeitPubKey: fPubKey,
            CheckpointTapScript: serverUnrollScript, 
            FeeTerms: GetArkFees(response.Fees)
        );

    }

    private ArkOperatorFeeTerms GetArkFees(FeeInfo response)
    {
        var fees = new ArkOperatorFeeTerms(
            TxFeeRate: Money.Zero, 
            OffchainOutput: Money.Zero,
            OnchainOutput: Money.Zero,
            OffchainInput: Money.Zero,
            OnchainInput: Money.Zero
        );

        if (decimal.TryParse(response?.TxFeeRate, out var txFeeRate))
            fees = fees with { TxFeeRate = Money.Satoshis(txFeeRate) };
        if (decimal.TryParse(response?.IntentFee.OffchainOutput, out var offchainOutputFee))
            fees = fees with { OffchainOutput = Money.Satoshis(offchainOutputFee) };
        if (decimal.TryParse(response?.IntentFee.OffchainInput, out var offchainInput))
            fees = fees with { OffchainInput = Money.Satoshis(offchainInput) };
        if (decimal.TryParse(response?.IntentFee.OnchainOutput, out var onchainOutputFee))
            fees = fees with { OnchainOutput = Money.Satoshis(onchainOutputFee) };
        if (decimal.TryParse(response?.IntentFee.OnchainInput, out var onchainInput))
            fees = fees with { OnchainInput = Money.Satoshis(onchainInput) };

        return fees;
    }
}