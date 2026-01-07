using BTCPayServer.Plugins.ArkPayServer.Storage;
using Microsoft.Extensions.Logging;
using NArk;
using NArk.Abstractions.Wallets;
using NArk.Contracts;
using NArk.Sweeper;
using NArk.Swaps.Helpers;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Services.Policies;

/// <summary>
/// Sweep policy for forwarding all spendable VTXOs to a wallet's configured destination address.
/// This applies to both legacy and HD wallets that have an explicit WalletDestination set.
/// </summary>
public class DestinationSweepPolicy(
    IWallet wallet,
    EfCoreWalletStorage walletStorage,
    ILogger<DestinationSweepPolicy> logger) : ISweepPolicy
{
    public bool CanSweep(IEnumerable<ArkUnspendableCoin> coins) =>
        coins.Any(c => c.Contract is ArkPaymentContract or HashLockedArkPaymentContract);

    public async IAsyncEnumerable<ArkCoin> SweepAsync(IEnumerable<ArkUnspendableCoin> coins)
    {
        // Filter to spendable contract types (not VHTLCContract - those are handled by SwapSweepPolicy)
        var spendableCoins = coins
            .Where(c => c.Contract is ArkPaymentContract or HashLockedArkPaymentContract)
            .ToArray();

        if (spendableCoins.Length == 0)
            yield break;

        // Group by wallet
        var walletIds = spendableCoins.Select(c => c.WalletIdentifier).Distinct().ToArray();

        // Load wallets via storage, filter for those with destinations set
        var wallets = await walletStorage.GetWalletsByIdsAsync(walletIds, CancellationToken.None);
        var walletsWithDestination = wallets
            .Where(w => !string.IsNullOrEmpty(w.WalletDestination))
            .ToDictionary(w => w.Id, w => w.WalletDestination!);

        foreach (var coin in spendableCoins)
        {
            if (!walletsWithDestination.TryGetValue(coin.WalletIdentifier, out var destinationStr))
                continue;

            // Parse destination address
            ArkAddress destination;
            try
            {
                destination = ArkAddress.Parse(destinationStr);
            }
            catch
            {
                logger.LogWarning("Invalid destination address for wallet {WalletId}: {Destination}",
                    coin.WalletIdentifier, destinationStr);
                continue;
            }

            // Skip coins already at destination to avoid infinite loops
            if (coin.TxOut.ScriptPubKey == destination.ScriptPubKey)
            {
                logger.LogTrace("Skipping coin {Outpoint} - already at destination", coin.Outpoint);
                continue;
            }

            var fingerprint = await wallet.GetWalletFingerprint(coin.WalletIdentifier);

            switch (coin.Contract)
            {
                case ArkPaymentContract paymentContract:
                {
                    var userFingerprint = paymentContract.User.Extract().WalletId;
                    if (!fingerprint.Equals(userFingerprint, StringComparison.OrdinalIgnoreCase))
                        continue;

                    yield return new ArkCoin(
                        walletIdentifier: coin.WalletIdentifier,
                        contract: paymentContract,
                        birth: coin.Birth,
                        expiresAt: coin.ExpiresAt,
                        expiresAtHeight: coin.ExpiresAtHeight,
                        outPoint: coin.Outpoint,
                        txOut: coin.TxOut,
                        signerDescriptor: paymentContract.User,
                        spendingScriptBuilder: paymentContract.CollaborativePath(),
                        spendingConditionWitness: null,
                        lockTime: null,
                        sequence: null,
                        recoverable: coin.Recoverable);
                    break;
                }

                case HashLockedArkPaymentContract hashlock when hashlock.User != null:
                {
                    var userFingerprint = hashlock.User.Extract().WalletId;
                    if (!fingerprint.Equals(userFingerprint, StringComparison.OrdinalIgnoreCase))
                        continue;

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
                    break;
                }
            }
        }
    }
}
