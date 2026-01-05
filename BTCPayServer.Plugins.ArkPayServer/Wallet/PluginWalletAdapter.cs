using System.Text;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Swaps.Helpers;
using NArk.Transport;
using NBitcoin;
using NBitcoin.Scripting;

namespace BTCPayServer.Plugins.ArkPayServer.Wallet;

/// <summary>
/// Adapter implementing NNark's IWallet interface using plugin's existing wallet infrastructure.
/// Bridges between NNark services and the plugin's wallet management.
/// </summary>
public class PluginWalletAdapter : IWallet
{
    private readonly IDbContextFactory<ArkPluginDbContext> _dbContextFactory;
    private readonly ArkadeWalletSignerProvider _signerProvider;
    private readonly IClientTransport _clientTransport;
    private readonly ISafetyService _safetyService;
    private readonly IWalletStorage _walletStorage;

    public PluginWalletAdapter(
        IDbContextFactory<ArkPluginDbContext> dbContextFactory,
        ArkadeWalletSignerProvider signerProvider,
        IClientTransport clientTransport,
        ISafetyService safetyService,
        IWalletStorage walletStorage)
    {
        _dbContextFactory = dbContextFactory;
        _signerProvider = signerProvider;
        _clientTransport = clientTransport;
        _safetyService = safetyService;
        _walletStorage = walletStorage;
    }

    public async Task CreateNewWallet(string walletIdentifier, CancellationToken cancellationToken = default)
    {
        await using var @lock = await _safetyService.LockKeyAsync($"wallet::{walletIdentifier}", cancellationToken);

        var network = (await _clientTransport.GetServerInfoAsync(cancellationToken)).Network;

        // Create new HD wallet with BIP-39 mnemonic
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
        var fingerprint = mnemonic.DeriveExtKey().GetPublicKey().GetHDFingerPrint();

        // Create wallet entity
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var wallet = new Data.Entities.ArkWallet
        {
            Id = walletIdentifier,
            Wallet = mnemonic.ToString(),
            WalletType = WalletType.HD,
            LastUsedIndex = 0,
            AccountDescriptor = GetAccountDescriptor(mnemonic.DeriveExtKey(), network, fingerprint)
        };

        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string> GetWalletFingerprint(string walletIdentifier, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var wallet = await db.Wallets.FirstOrDefaultAsync(w => w.Id == walletIdentifier, cancellationToken);
        if (wallet == null)
        {
            throw new KeyNotFoundException($"Wallet {walletIdentifier} not found");
        }

        return GetFingerprintFromWallet(wallet);
    }

    public async Task<ISigningEntity> GetNewSigningEntity(string walletIdentifier, CancellationToken cancellationToken = default)
    {
        await using var @lock = await _safetyService.LockKeyAsync($"wallet::{walletIdentifier}", cancellationToken);

        var network = (await _clientTransport.GetServerInfoAsync(cancellationToken)).Network;

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var wallet = await db.Wallets.FirstOrDefaultAsync(w => w.Id == walletIdentifier, cancellationToken);

        if (wallet == null)
        {
            throw new KeyNotFoundException($"Wallet {walletIdentifier} not found");
        }

        ISigningEntity signer;

        switch (wallet.WalletType)
        {
            case WalletType.HD:
                var mnemonic = new Mnemonic(wallet.Wallet);
                var extKey = mnemonic.DeriveExtKey();
                signer = new HdSigningEntity(extKey, network, wallet.LastUsedIndex);

                // Increment index for next use
                wallet.LastUsedIndex++;
                await db.SaveChangesAsync(cancellationToken);
                break;

            case WalletType.Legacy:
            default:
                signer = SingleKeySigningEntity.FromNsec(wallet.Wallet, network);
                break;
        }

        return signer;
    }

    public async Task<ISigningEntity> FindSigningEntity(OutputDescriptor outputDescriptor, CancellationToken cancellationToken = default)
    {
        var network = (await _clientTransport.GetServerInfoAsync(cancellationToken)).Network;
        var descriptorInfo = OutputDescriptorHelpers.Extract(outputDescriptor);

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // First, try to find by wallet ID from descriptor
        var walletId = descriptorInfo.WalletId;
        var wallet = await db.Wallets.FirstOrDefaultAsync(w => w.Id == walletId, cancellationToken);

        // If not found by ID, try to find by fingerprint in AccountDescriptor
        var fingerprint = descriptorInfo.AccountPath?.MasterFingerprint.ToString();
        if (wallet == null && !string.IsNullOrEmpty(fingerprint))
        {
            wallet = await db.Wallets.FirstOrDefaultAsync(
                w => w.AccountDescriptor != null && w.AccountDescriptor.Contains(fingerprint),
                cancellationToken);
        }

        // If still not found, try to find by matching the descriptor in contracts' ContractData["user"]
        if (wallet == null)
        {
            var descriptorString = outputDescriptor.ToString();
            // ContractData is stored as JSONB - query contracts and check in memory
            // This is a fallback path that should rarely be hit
            var contracts = await db.WalletContracts
                .Where(c => c.ContractData != null)
                .ToListAsync(cancellationToken);

            var contract = contracts.FirstOrDefault(
                c => c.GetSigningEntityDescriptor() == descriptorString);

            if (contract != null)
            {
                wallet = await db.Wallets.FirstOrDefaultAsync(w => w.Id == contract.WalletId, cancellationToken);
            }
        }

        if (wallet == null)
        {
            throw new KeyNotFoundException($"Could not find wallet for descriptor {outputDescriptor}");
        }

        switch (wallet.WalletType)
        {
            case WalletType.HD:
                var mnemonic = new Mnemonic(wallet.Wallet);
                var extKey = mnemonic.DeriveExtKey();
                return new HdSigningEntity(extKey, outputDescriptor);

            case WalletType.Legacy:
            default:
                return SingleKeySigningEntity.FromNsec(wallet.Wallet, network);
        }
    }

    private static string GetAccountDescriptor(ExtKey extKey, Network network, HDFingerprint fingerprint)
    {
        var coinType = network.ChainName == ChainName.Mainnet ? "0" : "1";
        var accountKeyPath = new KeyPath($"m/86'/{coinType}'/0'");
        var accountXpriv = extKey.Derive(accountKeyPath);
        var accountXpub = accountXpriv.Neuter().GetWif(network).ToWif();

        return $"tr([{fingerprint}/86'/{coinType}'/0']{accountXpub}/0/*)";
    }

    private static string GetFingerprintFromWallet(Data.Entities.ArkWallet wallet)
    {
        if (wallet.WalletType == WalletType.HD && !string.IsNullOrEmpty(wallet.AccountDescriptor))
        {
            // Extract fingerprint from descriptor: tr([fingerprint/86'/coin'/0']xpub...)
            var start = wallet.AccountDescriptor.IndexOf('[');
            var slash = wallet.AccountDescriptor.IndexOf('/');
            if (start >= 0 && slash > start)
            {
                return wallet.AccountDescriptor.Substring(start + 1, slash - start - 1);
            }
        }

        // For legacy wallets, the ID is the pubkey hex
        return wallet.Id;
    }

    /// <summary>
    /// Internal HD signing entity implementation for HD wallets.
    /// </summary>
    private class HdSigningEntity : ISigningEntity
    {
        private readonly ExtKey _extKey;
        private readonly OutputDescriptor _descriptor;

        public HdSigningEntity(ExtKey extKey, Network network, int index)
            : this(extKey, GetDescriptorFromIndex(extKey, network, index))
        {
        }

        public HdSigningEntity(ExtKey extKey, OutputDescriptor descriptor)
        {
            _extKey = extKey;
            _descriptor = descriptor;
        }

        public Task<Dictionary<string, string>> GetMetadata(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new Dictionary<string, string>
            {
                { "Descriptor", _extKey.GetPublicKey().ToHex() },
                { "Fingerprint", _extKey.GetPublicKey().GetHDFingerPrint().ToString() }
            });
        }

        public Task<string> GetFingerprint(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_extKey.GetPublicKey().GetHDFingerPrint().ToString());
        }

        public Task<OutputDescriptor> GetOutputDescriptor(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_descriptor);
        }

        public Task<NBitcoin.Secp256k1.ECPubKey> GetPublicKey(CancellationToken cancellationToken = default)
        {
            var info = OutputDescriptorHelpers.Extract(_descriptor);
            return Task.FromResult(info.PubKey!);
        }

        public Task<NBitcoin.Secp256k1.ECPrivKey> DerivePrivateKey(CancellationToken cancellationToken = default)
        {
            var info = OutputDescriptorHelpers.Extract(_descriptor);
            return Task.FromResult(NBitcoin.Secp256k1.ECPrivKey.Create(_extKey.Derive(info.FullPath!).PrivateKey.ToBytes()));
        }

        public async Task<SignResult> SignData(uint256 hash, CancellationToken cancellationToken = default)
        {
            var key = await DerivePrivateKey(cancellationToken);
            var sig = key.SignBIP340(hash.ToBytes());
            return new SignResult(sig, key.CreateXOnlyPubKey());
        }

        public async Task<NBitcoin.Secp256k1.Musig.MusigPartialSignature> SignMusig(
            NBitcoin.Secp256k1.Musig.MusigContext context,
            NBitcoin.Secp256k1.Musig.MusigPrivNonce nonce,
            CancellationToken cancellationToken = default)
        {
            return context.Sign(await DerivePrivateKey(cancellationToken), nonce);
        }

        private static OutputDescriptor GetDescriptorFromIndex(ExtKey extKey, Network network, int index)
        {
            var fingerprint = extKey.GetPublicKey().GetHDFingerPrint();
            var coinType = network.ChainName == ChainName.Mainnet ? "0" : "1";

            var accountKeyPath = new KeyPath($"m/86'/{coinType}'/0'");
            var accountXpriv = extKey.Derive(accountKeyPath);
            var accountXpub = accountXpriv.Neuter().GetWif(network).ToWif();

            var descriptor = $"tr([{fingerprint}/86'/{coinType}'/0']{accountXpub}/0/{index})";
            return OutputDescriptor.Parse(descriptor, network);
        }
    }
}
