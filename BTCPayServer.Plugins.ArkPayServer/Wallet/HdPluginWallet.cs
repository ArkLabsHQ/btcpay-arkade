using NArk.Abstractions.Wallets;
using NArk.Swaps.Helpers;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace BTCPayServer.Plugins.ArkPayServer.Wallet;

/// <summary>
/// IPluginWallet implementation for HD (BIP-39/BIP-86) wallets.
/// Uses BIP-86 (Taproot) derivation: m/86'/coin'/0'/0/*
/// </summary>
public class HdPluginWallet : IPluginWallet
{
    private readonly ExtKey _masterKey;
    private readonly Network _network;
    private readonly Func<int, Task> _onIndexUsed;
    private int _lastUsedIndex;

    public string WalletId { get; }
    public WalletType Type => WalletType.HD;

    public HdPluginWallet(
        Mnemonic mnemonic,
        Network network,
        int lastUsedIndex,
        Func<int, Task>? onIndexUsed = null)
    {
        _masterKey = mnemonic.DeriveExtKey();
        _network = network;
        _lastUsedIndex = lastUsedIndex;
        _onIndexUsed = onIndexUsed ?? (_ => Task.CompletedTask);

        WalletId = _masterKey.GetPublicKey().GetHDFingerPrint().ToString();
    }

    /// <summary>
    /// Creates an HD wallet from a BIP-39 mnemonic string.
    /// </summary>
    public static HdPluginWallet FromMnemonic(
        string mnemonicWords,
        Network network,
        int lastUsedIndex = 0,
        Func<int, Task>? onIndexUsed = null)
    {
        var mnemonic = new Mnemonic(mnemonicWords, Wordlist.English);
        return new HdPluginWallet(mnemonic, network, lastUsedIndex, onIndexUsed);
    }

    /// <summary>
    /// Generates a new HD wallet with a random mnemonic.
    /// </summary>
    public static (HdPluginWallet wallet, string mnemonic) CreateNew(
        Network network,
        WordCount wordCount = WordCount.Twelve,
        Func<int, Task>? onIndexUsed = null)
    {
        var mnemonic = new Mnemonic(Wordlist.English, wordCount);
        var wallet = new HdPluginWallet(mnemonic, network, 0, onIndexUsed);
        return (wallet, mnemonic.ToString());
    }

    /// <summary>
    /// Gets a new signing entity by incrementing the derivation index.
    /// Each call returns a unique key for a new payment contract.
    /// </summary>
    public async Task<ISigningEntity> GetNewSigningEntity(CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref _lastUsedIndex);
        await _onIndexUsed(index);
        return new HdSigningEntity(_masterKey, _network, index);
    }

    /// <summary>
    /// Finds the signing entity for an existing contract by extracting the derivation path.
    /// </summary>
    public Task<ISigningEntity?> FindSigningEntity(OutputDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = OutputDescriptorHelpers.Extract(descriptor);

            // Check if this descriptor belongs to our wallet
            var ourFingerprint = _masterKey.GetPublicKey().GetHDFingerPrint().ToString();
            if (metadata.AccountPath?.MasterFingerprint.ToString() != ourFingerprint)
            {
                return Task.FromResult<ISigningEntity?>(null);
            }

            return Task.FromResult<ISigningEntity?>(new HdSigningEntity(_masterKey, descriptor));
        }
        catch
        {
            return Task.FromResult<ISigningEntity?>(null);
        }
    }

    /// <summary>
    /// Gets the default receive descriptor (index 0).
    /// </summary>
    public Task<OutputDescriptor> GetDefaultReceiveDescriptor(CancellationToken cancellationToken = default)
    {
        var entity = new HdSigningEntity(_masterKey, _network, 0);
        return entity.GetOutputDescriptor(cancellationToken);
    }

    /// <summary>
    /// Internal HD signing entity implementation.
    /// </summary>
    private class HdSigningEntity : ISigningEntity
    {
        private readonly ExtKey _masterKey;
        private readonly OutputDescriptor _descriptor;

        public HdSigningEntity(ExtKey masterKey, Network network, int index)
        {
            _masterKey = masterKey;
            _descriptor = GetDescriptorFromIndex(masterKey, network, index);
        }

        public HdSigningEntity(ExtKey masterKey, OutputDescriptor descriptor)
        {
            _masterKey = masterKey;
            _descriptor = descriptor;
        }

        public Task<Dictionary<string, string>> GetMetadata(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new Dictionary<string, string>
            {
                ["type"] = "hd",
                ["fingerprint"] = _masterKey.GetPublicKey().GetHDFingerPrint().ToString(),
                ["descriptor"] = _descriptor.ToString()
            });
        }

        public Task<string> GetFingerprint(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_masterKey.GetPublicKey().GetHDFingerPrint().ToString());
        }

        public Task<OutputDescriptor> GetOutputDescriptor(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_descriptor);
        }

        public Task<ECPubKey> GetPublicKey(CancellationToken cancellationToken = default)
        {
            var info = OutputDescriptorHelpers.Extract(_descriptor);
            return Task.FromResult(info.PubKey!);
        }

        public Task<ECPrivKey> DerivePrivateKey(CancellationToken cancellationToken = default)
        {
            var info = OutputDescriptorHelpers.Extract(_descriptor);
            var derivedKey = _masterKey.Derive(info.FullPath!);
            return Task.FromResult(ECPrivKey.Create(derivedKey.PrivateKey.ToBytes()));
        }

        public async Task<SignResult> SignData(uint256 hash, CancellationToken cancellationToken = default)
        {
            var key = await DerivePrivateKey(cancellationToken);
            var sig = key.SignBIP340(hash.ToBytes());
            return new SignResult(sig, key.CreateXOnlyPubKey());
        }

        public async Task<MusigPartialSignature> SignMusig(
            MusigContext context,
            MusigPrivNonce nonce,
            CancellationToken cancellationToken = default)
        {
            var key = await DerivePrivateKey(cancellationToken);
            return context.Sign(key, nonce);
        }

        private static OutputDescriptor GetDescriptorFromIndex(ExtKey masterKey, Network network, int index)
        {
            var fingerprint = masterKey.GetPublicKey().GetHDFingerPrint();
            var coinType = network.ChainName == ChainName.Mainnet ? "0" : "1";

            // BIP-86 Taproot: m/86'/coin'/0'
            var accountKeyPath = new KeyPath($"m/86'/{coinType}'/0'");
            var accountXpriv = masterKey.Derive(accountKeyPath);
            var accountXpub = accountXpriv.Neuter().GetWif(network).ToWif();

            // Descriptor format: tr([fingerprint/86'/coin'/0']xpub/0/index)
            var descriptor = $"tr([{fingerprint}/86'/{coinType}'/0']{accountXpub}/0/{index})";

            return OutputDescriptor.Parse(descriptor, network);
        }
    }
}
