using BTCPayServer.Plugins.ArkPayServer.Storage;
using NArk.Abstractions;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Core.Transport;

namespace BTCPayServer.Plugins.ArkPayServer.Wallet;

/// <summary>
/// Adapter implementing NNark's IWallet interface using plugin's existing wallet infrastructure.
/// Bridges between NNark services and the plugin's wallet management.
/// </summary>
public class PluginWalletAdapter(
    IClientTransport clientTransport,
    ISafetyService safetyService,
    EfCoreWalletStorage walletStorage)
    : IWalletProvider
{
    public async Task<IArkadeWalletSigner?> GetSignerAsync(string identifier, CancellationToken cancellationToken = default)
    {
        try
        {
            var wallet = await walletStorage.LoadWallet(identifier, cancellationToken);
            return wallet.WalletType switch
            {
                WalletType.HD => new HierarchicalDeterministicWalletSigner(wallet),
                WalletType.SingleKey => NSecWalletSigner.FromNsec(wallet.Wallet),
                _ => throw new ArgumentOutOfRangeException(nameof(wallet.WalletType))
            };
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        
    }

    public async Task<IArkadeAddressProvider?> GetAddressProviderAsync(string identifier, CancellationToken cancellationToken = default)
    {
        try
        {
            var network = (await clientTransport.GetServerInfoAsync(cancellationToken)).Network;
            var wallet = await walletStorage.LoadWallet(identifier, cancellationToken);
            ArkAddress? sweepDestination = null;
            if(!string.IsNullOrEmpty(wallet.WalletDestination))
            {
                sweepDestination = ArkAddress.Parse(wallet.WalletDestination);
            }
            return wallet.WalletType switch
            {
                WalletType.HD => new HierarchicalDeterministicAddressProvider(clientTransport,safetyService, walletStorage, wallet, network, sweepDestination),
                WalletType.SingleKey => new SingleKeyAddressProvider(clientTransport,wallet, network,sweepDestination),
                _ => throw new ArgumentOutOfRangeException(nameof(wallet.WalletType))
            };
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }
}