using System.Runtime.CompilerServices;
using BTCPayServer.Plugins.ArkPayServer.Storage;
using BTCPayServer.Plugins.ArkPayServer.Wallet;
using NArk.Abstractions;
using NArk.Core.Contracts;
using NArk.Core.Sweeper;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Scripting;

namespace BTCPayServer.Plugins.ArkPayServer.Services.Policies;

/// <summary>
/// Sweep policy for forwarding all spendable VTXOs to a wallet's configured destination address.
/// This applies to both legacy and HD wallets that have an explicit WalletDestination set.
/// For SingleKey wallets without a destination, sweeps non-default coins (e.g. HashLocked)
/// to the wallet's default payment address.
/// </summary>
public class DestinationSweepPolicy(EfCoreWalletStorage walletStorage, IClientTransport clientTransport) : ISweepPolicy
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

        //if the wallet is Legacy (SingleKey), always sweep
        //if the wallet is HD, sweep if the wallet has a destination set
        // Load wallets via storage, filter for those with destinations set
        var wallets = await walletStorage.GetWalletsByIdsAsync(walletIds, cancellationToken);

        var eligibleWallets = wallets
            .Where(w => !string.IsNullOrEmpty(w.WalletDestination) || w.WalletType == WalletType.SingleKey)
            .ToDictionary(w => w.Id, w => w.WalletDestination);

        foreach (var group in spendableCoins
                     .Where(c => eligibleWallets.ContainsKey(c.WalletIdentifier))
                     .GroupBy(c => c.WalletIdentifier))
        {
            var wallet = wallets.First(w => w.Id == group.Key);
            Script destinationScript;

            if (!string.IsNullOrEmpty(eligibleWallets[group.Key]))
            {
                destinationScript = ArkAddress.Parse(eligibleWallets[group.Key]!).ScriptPubKey;
            }
            else if (wallet.WalletType == WalletType.SingleKey)
            {
                // SingleKey wallet with no explicit destination:
                // construct the default payment contract address (same one created on wallet setup)
                // so we only sweep coins NOT already at this address (e.g. HashLocked receive coins)
                var info = await clientTransport.GetServerInfoAsync(cancellationToken);
                var descriptor = OutputDescriptor.Parse(wallet.AccountDescriptor, info.Network);
                var defaultContract = new ArkPaymentContract(info.SignerKey, info.UnilateralExit, descriptor);
                destinationScript = defaultContract.GetArkAddress().ScriptPubKey;
            }
            else
            {
                continue;
            }

            // Skip coins already at the destination — avoids circular sweep
            foreach (var walletCoin in group.Where(c => c.TxOut.ScriptPubKey != destinationScript))
            {
                yield return walletCoin;
            }
        }
    }
}
