using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Storage;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Swaps.Helpers;
using NBitcoin;
using NBitcoin.Scripting;

namespace BTCPayServer.Plugins.ArkPayServer.Wallet;

public class HierarchicalDeterministicAddressProvider(
    ISafetyService safetyService,
    EfCoreWalletStorage walletStorage,
    ArkWallet wallet,
    Network network)
    : IArkadeAddressProvider
{
    public async Task<string> GetWalletFingerprint(CancellationToken cancellationToken = default)
    {
        var descriptor = OutputDescriptor.Parse(wallet.AccountDescriptor ?? throw new Exception("Malformed HD Wallet"), network);
        return descriptor.Extract().WalletId;
    }

    public async Task<OutputDescriptor> GetNewSigningDescriptor(string identifier, CancellationToken cancellationToken = default)
    {
        await using var @lock = await safetyService.LockKeyAsync($"wallet::{identifier}", cancellationToken);

        var descriptor =
            GetDescriptorFromIndex(
                network,
                wallet.AccountDescriptor ?? throw new Exception("Malformed HD Wallet"),
                wallet.LastUsedIndex++
            );
        
        if (identifier != descriptor.Extract().WalletId) 
            throw new ArgumentException(nameof(identifier));

        await walletStorage.SaveWallet(wallet.Id, wallet, wallet.AccountDescriptor, cancellationToken);
        
        return descriptor;

        static OutputDescriptor GetDescriptorFromIndex(Network network, string descriptor, int index)
        {
            return OutputDescriptor.Parse(descriptor.Replace("/*", $"/{index}"), network);
        }
    }
}