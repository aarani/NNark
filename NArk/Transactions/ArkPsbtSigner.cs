using NArk.Abstractions.Wallets;
using NArk.Helpers;
using NBitcoin;

namespace NArk.Transactions;

public record ArkPsbtSigner(ArkCoin Coin, ISigningEntity SigningEntity)
{
    public async Task SignAndFillPsbt(PSBT psbt,
        TaprootReadyPrecomputedTransactionData precomputedTransactionData,
        CancellationToken cancellationToken = default,
        TaprootSigHash sigHash = TaprootSigHash.Default)
    {
        var psbtInput = Coin.FillPsbtInput(psbt);
        
        if (psbtInput is null)
            return;

        var gtx = psbt.GetGlobalTransaction();
        var hash = gtx.GetSignatureHashTaproot(precomputedTransactionData,
            new TaprootExecutionData((int) psbtInput.Index, Coin.SpendingScript.LeafHash)
            {
                SigHash = sigHash
            });

        var (sig, ourKey) = await SigningEntity.SignData(hash);

        psbtInput.SetTaprootScriptSpendSignature(ourKey, Coin.SpendingScript.LeafHash, sig);
    }
}