using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Storage;
using NArk.Abstractions;
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
    Network network,
    ArkAddress? sweepDestination)
    : IArkadeAddressProvider
{
    public async Task<bool> IsOurs(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        OutputDescriptor.Parse(wallet.AccountDescriptor ?? throw new Exception("Malformed HD Wallet"), network);
        var index = descriptor.Extract().DerivationPath?.Indexes.Last().ToString();
        if (index is null)
        {
            return false;
        }
        var expected =    GetDescriptorFromIndex(network, wallet.AccountDescriptor, Convert.ToInt32(index));
        return expected.Equals(descriptor);
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
        
        if (identifier != wallet.AccountDescriptor) 
            throw new ArgumentException(nameof(identifier));

        await walletStorage.SaveWallet(wallet.Id, wallet, wallet.AccountDescriptor, cancellationToken);
        
        return descriptor;

      
    }
    
    private static OutputDescriptor GetDescriptorFromIndex(Network network, string descriptor, int index)
    {
        //TODO: the checksum may need to be recomputed?
        return OutputDescriptor.Parse(descriptor.Replace("/*", $"/{index}"), network);
    }

    public async Task<ArkContract> GetNextContract(string identifier, NextContractPurpose purpose, CancellationToken cancellationToken = default)
    {
        var info = await transport.GetServerInfoAsync(cancellationToken);
        if (purpose == NextContractPurpose.SendToSelf && sweepDestination is not null)
        {
            return new UnknownArkContract(sweepDestination, info.SignerKey, info.Network.ChainName == ChainName.Mainnet);
        }
        
        var signingDescriptor = await GetNextSigningDescriptor(identifier, cancellationToken);
        return new ArkPaymentContract(
            info.SignerKey,
            info.UnilateralExit,
            signingDescriptor
        );
    }
}