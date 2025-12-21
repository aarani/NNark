using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Helpers;
using NArk.Transactions;
using NArk.Transport;
using NBitcoin;

namespace NArk.Services;

public class SpendingService (
    IVtxoStorage vtxoStorage,
    IContractStorage contractStorage,
    ISigningService signingService,
    IClientTransport transport
)
{
    public async Task<uint256> Spend(string walletId, TxOut[] outputs, CancellationToken cancellationToken = default)
    {
        var serverInfo = await transport.GetServerInfoAsync(cancellationToken);
        
        var outputsSumInSatoshis = outputs.Sum(o => o.Value);
        
        // Check if any output is explicitly subdust (user wants to send subdust amount)
        var hasExplicitSubdustOutput = outputs.Count(o => o.Value < serverInfo.Dust);
        
        var contracts = await contractStorage.LoadActiveContracts([walletId]);
        var contractByScript =
            contracts
                .GroupBy(c => c.Script)
                .ToDictionary(g => g.Key, g => g.First());
        var vtxos = await vtxoStorage.GetVtxosByScripts([..contracts.Select(c => c.Script)]);
        var vtxosByContracts =
            vtxos
                .GroupBy(v => contractByScript[v.Script]);
        
        HashSet<ArkPsbtSigner> coins = [];
        foreach (var vtxosByContract in vtxosByContracts)
        {
            foreach (var vtxo in vtxosByContract)
            {
                coins.Add(await signingService.GetVtxoPsbtSignerByContract(vtxosByContract.Key, vtxo));
            }
        }

        var selectedCoins = CoinSelectionHelper.SelectCoins([..coins], outputsSumInSatoshis, serverInfo.Dust, hasExplicitSubdustOutput);

        var transactionBuilder = new TransactionHelpers.ArkTransactionBuilder(transport);

        return await transactionBuilder.ConstructAndSubmitArkTransaction(selectedCoins, outputs, cancellationToken);
    }
}