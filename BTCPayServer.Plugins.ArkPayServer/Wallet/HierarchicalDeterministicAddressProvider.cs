using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Storage;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Contracts;
using NArk.Swaps.Helpers;
using NArk.Transport;
using NBitcoin;
using NBitcoin.Scripting;

namespace BTCPayServer.Plugins.ArkPayServer.Wallet;

public class HierarchicalDeterministicAddressProvider(
    IClientTransport transport,
    ISafetyService safetyService,
    EfCoreWalletStorage walletStorage,
    ArkWallet wallet,
    Network network)
    : IArkadeAddressProvider
{
    public async Task<bool> IsOurs(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        var walletDescriptor = OutputDescriptor.Parse(wallet.AccountDescriptor ?? throw new Exception("Malformed HD Wallet"), network);
        return walletDescriptor.Extract().WalletId == descriptor.Extract().WalletId;
    }

    public async Task<OutputDescriptor> GetNextSigningDescriptor(string identifier, CancellationToken cancellationToken = default)
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

    public async Task<ArkContract> GetNextPaymentContract(string identifier, CancellationToken cancellationToken = default)
    {
        var info = await transport.GetServerInfoAsync(cancellationToken);
        var signingDescriptor = await GetNextSigningDescriptor(identifier, cancellationToken);
        return new ArkPaymentContract(
            info.SignerKey,
            info.UnilateralExit,
            signingDescriptor
        );
    }
}