using NArk.Abstractions;
using NArk.Abstractions.Intents;
using NArk.Transactions;
using NBitcoin;

namespace NArk.Services;

public class OnchainService(ISigningService signingService, IIntentGenerationService intentGenerationService) : IOnchainService
{
    public async Task<Guid> InitiateCollaborativeExit(ArkCoin[] inputs, ArkTxOut[] outputs,
        CancellationToken cancellationToken = default)
    {
        List<ArkPsbtSigner> inputSigners = [];
        foreach (var input in inputs)
            inputSigners.Add(await signingService.GetPsbtSigner(input, cancellationToken));
        return await InitiateCollaborativeExit(inputSigners.ToArray(), outputs, cancellationToken);
    }

    public async Task<Guid> InitiateCollaborativeExit(ArkPsbtSigner[] inputs, ArkTxOut[] outputs, CancellationToken cancellationToken = default)
    {
        if (outputs.All(o => o.Type == ArkTxOutType.Vtxo))
            throw new InvalidOperationException("No on-chain outputs provided for collaborative exit.");
        if (inputs.Select(i => i.Coin.WalletIdentifier).Distinct().Count() != 1)
            throw new InvalidOperationException("All inputs must belong to the same wallet for collaborative exit.");
        
        var inputSigners =
            inputs.ToDictionary(signer => signer.Coin.ToLite(), signer => signer);
        
        var intentSpec = new ArkIntentSpec(
            [.. inputSigners.Keys],
            outputs,
            DateTime.UtcNow,
            DateTime.UtcNow.AddHours(1)
        );
        
        var intent =
            await intentGenerationService.GenerateManualIntent(inputs[0].Coin.WalletIdentifier, intentSpec, inputSigners, cancellationToken);

        return intent;
    }
}