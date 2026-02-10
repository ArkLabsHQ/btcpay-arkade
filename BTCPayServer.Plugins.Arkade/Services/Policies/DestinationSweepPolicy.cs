using System.Runtime.CompilerServices;
using BTCPayServer.Plugins.Arkade.Storage;
using BTCPayServer.Plugins.Arkade.Wallet;
using NArk.Abstractions;
using NArk.Core.Contracts;
using NArk.Core.Sweeper;

namespace BTCPayServer.Plugins.Arkade.Services.Policies;

/// <summary>
/// Sweep policy for forwarding all spendable VTXOs to a wallet's configured destination address.
/// This applies to both legacy and HD wallets that have an explicit WalletDestination set.
/// </summary>
public class DestinationSweepPolicy(EfCoreWalletStorage walletStorage) : ISweepPolicy
{
    public async IAsyncEnumerable<ArkCoin> SweepAsync(IEnumerable<ArkCoin> coins,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Filter to spendable contract types (not VHTLCContract - those are handled by SwapSweepPolicy)
        var spendableCoins = coins
            .Where(c => c.Contract is ArkPaymentContract or HashLockedArkPaymentContract)
            .ToArray();

        if (spendableCoins.Length == 0)
            yield break;

        // Group by wallet
        var walletIds = spendableCoins.Select(c => c.WalletIdentifier).ToHashSet();

        //if the wallet if Legacy, always sweep
        //if the wallet is HD, sweep if the wallet has a destination set
        // Load wallets via storage, filter for those with destinations set
        var wallets = await walletStorage.GetWalletsByIdsAsync(walletIds, cancellationToken);

        var eligibleWallets = wallets
            .Where(w => !string.IsNullOrEmpty(w.WalletDestination) || w.WalletType == WalletType.SingleKey)
            .ToDictionary(w => w.Id, w => w.WalletDestination);


        foreach (var coin in spendableCoins
                     .Where(c => eligibleWallets.ContainsKey(c.WalletIdentifier))
                     .GroupBy(c => c.WalletIdentifier))
        {
            var walletCoins = coin.ToList();
            if (!string.IsNullOrEmpty(eligibleWallets[coin.Key]))
            {
                walletCoins = walletCoins.Where(c =>
                    c.TxOut.ScriptPubKey != ArkAddress.Parse(eligibleWallets[coin.Key]).ScriptPubKey).ToList();
            }

            foreach (var walletCoin in walletCoins)
            {
                yield return walletCoin;
            }
        }
    }
}