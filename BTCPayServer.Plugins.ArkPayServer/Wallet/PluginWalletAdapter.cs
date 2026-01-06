using System.Text;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Swaps.Helpers;
using NArk.Transport;
using NArk.Wallets;
using NBitcoin;
using NBitcoin.Scripting;

namespace BTCPayServer.Plugins.ArkPayServer.Wallet;

/// <summary>
/// Adapter implementing NNark's IWallet interface using plugin's existing wallet infrastructure.
/// Bridges between NNark services and the plugin's wallet management.
/// </summary>
public class PluginWalletAdapter(
    IClientTransport clientTransport,
    ISafetyService safetyService,
    IWalletStorage walletStorage)
    : IWallet
{

    // Seed wallet is supported by NNark, Legacy is not
    private readonly SimpleSeedWallet _hdWallet = new(safetyService, clientTransport, walletStorage);

    public async Task CreateNewWallet(string walletIdentifier, CancellationToken cancellationToken = default)
    {
        await using var @lock = await safetyService.LockKeyAsync($"wallet::{walletIdentifier}", cancellationToken);

        // Create new HD wallet with BIP-39 mnemonic
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
        var fingerprint = mnemonic.DeriveExtKey().GetPublicKey().GetHDFingerPrint().ToString();

        await walletStorage.SaveWallet(walletIdentifier,
            new ArkWallet(walletIdentifier, fingerprint, Encoding.UTF8.GetBytes(mnemonic.ToString())), fingerprint,
            cancellationToken);
    }

    public async Task<string> GetWalletFingerprint(string walletIdentifier, CancellationToken cancellationToken = default)
    {
        var wallet = await walletStorage.LoadWallet(walletIdentifier, cancellationToken);
        var walletType = wallet.GetWalletType();
        
        if (walletType is WalletType.HD && !string.IsNullOrEmpty(wallet.WalletFingerprint))
        {
            return wallet.WalletFingerprint;
        }

        // For legacy wallets, the ID is the pubkey hex
        return wallet.WalletIdentifier;
    }

    public async Task<ISigningEntity> GetNewSigningEntity(string walletIdentifier, CancellationToken cancellationToken = default)
    {
        var network = (await clientTransport.GetServerInfoAsync(cancellationToken)).Network;

        var wallet = await walletStorage.LoadWallet(walletIdentifier, cancellationToken);

        var signer = wallet.GetWalletType() switch
        {
            WalletType.HD => await _hdWallet.GetNewSigningEntity(walletIdentifier, cancellationToken),
            _ => SingleKeySigningEntity.FromNsec(Encoding.UTF8.GetString(wallet.WalletPrivateBytes), network)
        };

        return signer;
    }

    public async Task<ISigningEntity> FindSigningEntity(OutputDescriptor outputDescriptor, CancellationToken cancellationToken = default)
    {
        var network = (await clientTransport.GetServerInfoAsync(cancellationToken)).Network;
        
        var walletId = outputDescriptor.Extract().WalletId;
        var wallet = await walletStorage.LoadWallet(walletId, cancellationToken);

        if (wallet == null)
        {
            throw new KeyNotFoundException($"Could not find wallet for descriptor {outputDescriptor}");
        }

        return wallet.GetWalletType() switch
        {
            WalletType.HD => await _hdWallet.FindSigningEntity(outputDescriptor, cancellationToken),
            _ => SingleKeySigningEntity.FromNsec(Encoding.UTF8.GetString(wallet.WalletPrivateBytes), network)
        };
    }

}
