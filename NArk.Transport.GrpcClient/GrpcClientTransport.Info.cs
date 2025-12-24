using Ark.V1;
using NArk.Extensions;
using NArk.Scripts;
using NArk.Transport.GrpcClient.Extensions;
using NBitcoin;

namespace NArk.Transport.GrpcClient;

public partial class GrpcClientTransport
{
    private static Sequence ParseSequence(long val)
    {
        return val >= 512 ? new Sequence(TimeSpan.FromSeconds(val)) : new Sequence((int)val);
    }

    public async Task<ArkServerInfo> GetServerInfoAsync(CancellationToken cancellationToken = default)
    {
        var response = await _serviceClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: cancellationToken);
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
            SignerKey: KeyExtensions.ParseOutputDescriptor(response.SignerPubkey, network),
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
            IntentOffchainOutput: Money.Zero,
            IntentOnchainOutput: Money.Zero,
            IntentOffchainInput: Money.Zero,
            IntentOnchainInput: Money.Zero
        );

        if (decimal.TryParse(response?.TxFeeRate, out var txFeeRate))
            fees = fees with { TxFeeRate = Money.Satoshis(txFeeRate) };
        if (decimal.TryParse(response?.IntentFee.OffchainOutput, out var offchainOutputFee))
            fees = fees with { IntentOffchainOutput = Money.Satoshis(offchainOutputFee) };
        if (decimal.TryParse(response?.IntentFee.OffchainInput, out var offchainInput))
            fees = fees with { IntentOffchainInput = Money.Satoshis(offchainInput) };
        if (decimal.TryParse(response?.IntentFee.OnchainOutput, out var onchainOutputFee))
            fees = fees with { IntentOnchainOutput = Money.Satoshis(onchainOutputFee) };
        if (decimal.TryParse(response?.IntentFee.OnchainInput, out var onchainInput))
            fees = fees with { IntentOnchainInput = Money.Satoshis(onchainInput) };

        return fees;
    }
}