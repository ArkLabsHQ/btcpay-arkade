using BTCPayServer.Plugins.ArkPayServer.Storage;
using BTCPayServer.Plugins.ArkPayServer.Wallet;
using NArk;
using NArk.Abstractions.Wallets;
using NArk.Contracts;
using NArk.Sweeper;
using NArk.Swaps.Helpers;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Services.Policies;

/// <summary>
/// Sweep policy for claiming HashLockedArkPaymentContract VTXOs in legacy wallets.
/// These contracts have a preimage that allows them to be claimed immediately.
/// </summary>
public class HashlockPaymentSweepPolicy(
    IWallet wallet,
    EfCoreWalletStorage walletStorage) : ISweepPolicy
{
    public bool CanSweep(IEnumerable<ArkUnspendableCoin> coins) =>
        coins.Any(c => c.Contract is HashLockedArkPaymentContract);

    public async IAsyncEnumerable<ArkCoin> SweepAsync(IEnumerable<ArkUnspendableCoin> coins)
    {
        var hashlockCoins = coins.Where(c => c.Contract is HashLockedArkPaymentContract).ToArray();
        if (hashlockCoins.Length == 0)
            yield break;

        // Group by wallet to batch lookups
        var walletIds = hashlockCoins.Select(c => c.WalletIdentifier).Distinct().ToArray();

        // Load wallets via storage, filter for legacy wallets only
        var wallets = await walletStorage.GetWalletsByIdsAsync(walletIds, CancellationToken.None);
        var legacyWalletIds = wallets
            .Where(w => w.WalletType == WalletType.Legacy)
            .Select(w => w.Id)
            .ToHashSet();

        foreach (var coin in hashlockCoins)
        {
            if (!legacyWalletIds.Contains(coin.WalletIdentifier))
                continue;

            if (coin.Contract is not HashLockedArkPaymentContract hashlock)
                continue;

            // User key is required for hashlock contracts
            if (hashlock.User == null)
                continue;

            var fingerprint = await wallet.GetWalletFingerprint(coin.WalletIdentifier);
            var userFingerprint = hashlock.User.Extract().WalletId;

            // Only sweep if this wallet owns the user key
            if (!fingerprint.Equals(userFingerprint, StringComparison.OrdinalIgnoreCase))
                continue;

            // Hashlock contracts can always be claimed immediately with the preimage
            yield return new ArkCoin(
                walletIdentifier: coin.WalletIdentifier,
                contract: hashlock,
                birth: coin.Birth,
                expiresAt: coin.ExpiresAt,
                expiresAtHeight: coin.ExpiresAtHeight,
                outPoint: coin.Outpoint,
                txOut: coin.TxOut,
                signerDescriptor: hashlock.User,
                spendingScriptBuilder: hashlock.CreateClaimScript(),
                spendingConditionWitness: new WitScript(Op.GetPushOp(hashlock.Preimage)),
                lockTime: null,
                sequence: null,
                recoverable: coin.Recoverable);
        }
    }
}
