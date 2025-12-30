using NArk.Abstractions.Wallets;
using NArk.Contracts;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Helpers;
using NArk.Sweeper;
using NBitcoin;

namespace NArk.Swaps.Policies;

public class SwapSweepPolicy(IWallet wallet, ISwapStorage swapStorage) : ISweepPolicy
{
    // Lets use this as IsPolicyEnabled for now...
    public bool CanSweep(IEnumerable<ArkUnspendableCoin> coins) =>
        coins.Any(c => c.Contract is VHTLCContract);
    public async IAsyncEnumerable<ArkCoin> SweepAsync(IEnumerable<ArkUnspendableCoin> coins)
    {
        var activeSwaps = await swapStorage.GetActiveSwaps();
        coins = coins.Where(c => c.Contract is VHTLCContract);
        var coinSwapPairs = coins.Join(activeSwaps, c => c.Contract.ToString(), s => s.ContractScript, ((Coin, Swap) => (Coin, Swap)));
        foreach (var (Coin, _) in coinSwapPairs)
        {
            if (Coin.Contract is not VHTLCContract htlc) continue;

            var fingerprint = await wallet.GetWalletFingerprint(Coin.WalletIdentifier);

            if (htlc.Preimage is not null && OutputDescriptorHelpers.GetFingerprint(htlc.Receiver).Equals(fingerprint, StringComparison.InvariantCultureIgnoreCase))
            {
                yield return new ArkCoin(Coin.WalletIdentifier, htlc, Coin.ExpiresAt, Coin.ExpiresAtHeight, Coin.OutPoint, Coin.TxOut, htlc.Receiver,
                    htlc.CreateClaimScript(), new WitScript(Op.GetPushOp(htlc.Preimage!)), null, null, Coin.Recoverable);
            }

            if (htlc.RefundLocktime.IsTimeLock &&
                htlc.RefundLocktime.Date < DateTime.UtcNow && OutputDescriptorHelpers.GetFingerprint(htlc.Sender).Equals(fingerprint, StringComparison.InvariantCultureIgnoreCase))
            {
                yield return new ArkCoin(Coin.WalletIdentifier, htlc, Coin.ExpiresAt, Coin.ExpiresAtHeight, Coin.OutPoint, Coin.TxOut, htlc.Receiver,
                    htlc.CreateRefundWithoutReceiverScript(), null, htlc.RefundLocktime, null, Coin.Recoverable);
            }
        }
    }
}