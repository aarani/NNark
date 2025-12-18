using NArk.Abstractions.Wallets;
using NArk.Helpers;
using NBitcoin;

namespace NArk.Transactions;

public class ArkPsbtSigner(ArkCoin    coin, ISigningEntity signingEntity)
{
    /*
    public void SignAndFillPsbt(
        PSBT psbt,
        TaprootReadyPrecomputedTransactionData precomputedTransactionData,
        TaprootSigHash sigHash = TaprootSigHash.Default
    )
    {
        var psbtInput = coin.FillPsbtInput(psbt);
        
        if (psbtInput is null)
            return;

        var gtx = psbt.GetGlobalTransaction();
        var hash = gtx.GetSignatureHashTaproot(precomputedTransactionData,
            new TaprootExecutionData((int) psbtInput.Index, coin.SpendingScript.LeafHash)
            {
                SigHash = sigHash
            });

        var (sig, ourKey) = signingEntity.SignData(hash);

        psbtInput.SetTaprootScriptSpendSignature(ourKey, coin.SpendingScript.LeafHash, sig);
    }
    */
}