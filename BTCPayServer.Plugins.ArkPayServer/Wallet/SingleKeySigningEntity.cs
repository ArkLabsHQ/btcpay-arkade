using NArk.Abstractions.Wallets;
using NArk.Extensions;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace BTCPayServer.Plugins.ArkPayServer.Wallet;

/// <summary>
/// ISigningEntity implementation for legacy single-key wallets.
/// Wraps an ECPrivKey and provides signing capabilities.
/// </summary>
public class SingleKeySigningEntity : ISigningEntity
{
    private readonly ECPrivKey _privateKey;
    private readonly ECPubKey _publicKey;
    private readonly ECXOnlyPubKey _xOnlyPubKey;
    private readonly OutputDescriptor _outputDescriptor;
    private readonly string _fingerprint;
    private readonly Network _network;

    public SingleKeySigningEntity(ECPrivKey privateKey, Network network)
    {
        _privateKey = privateKey;
        _network = network;
        _publicKey = privateKey.CreatePubKey();
        _xOnlyPubKey = _publicKey.ToXOnlyPubKey();

        // Create a simple taproot descriptor: tr(pubkey_hex)
        var pubKeyHex = _xOnlyPubKey.ToBytes().ToHexStringLower();
        var taprootPubKey = new TaprootPubKey(_xOnlyPubKey.ToBytes());
        _outputDescriptor = OutputDescriptor.Parse($"tr({pubKeyHex})", network);

        // For single-key wallets, the fingerprint is the pubkey hex
        _fingerprint = pubKeyHex;
    }

    /// <summary>
    /// Creates a SingleKeySigningEntity from an nsec (Nostr-style bech32 private key).
    /// </summary>
    public static SingleKeySigningEntity FromNsec(string nsec, Network network)
    {
        var decoded = Bech32Encoder.ExtractWitnessFromBech32("nsec", nsec, Bech32Encoder.Encoding.Bech32);
        var privKey = ECPrivKey.Create(decoded);
        return new SingleKeySigningEntity(privKey, network);
    }

    /// <summary>
    /// Creates a SingleKeySigningEntity from a hex-encoded private key.
    /// </summary>
    public static SingleKeySigningEntity FromHex(string hexPrivateKey, Network network)
    {
        var bytes = Convert.FromHexString(hexPrivateKey);
        var privKey = ECPrivKey.Create(bytes);
        return new SingleKeySigningEntity(privKey, network);
    }

    public Task<Dictionary<string, string>> GetMetadata(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new Dictionary<string, string>
        {
            ["type"] = "single-key",
            ["pubkey"] = _xOnlyPubKey.ToBytes().ToHexStringLower()
        });
    }

    public Task<string> GetFingerprint(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_fingerprint);
    }

    public Task<OutputDescriptor> GetOutputDescriptor(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_outputDescriptor);
    }

    public Task<ECPubKey> GetPublicKey(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_publicKey);
    }

    public Task<ECPrivKey> DerivePrivateKey(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_privateKey);
    }

    public Task<SignResult> SignData(uint256 hash, CancellationToken cancellationToken = default)
    {
        if (!_privateKey.TrySignBIP340(hash.ToBytes(), null, out var sig))
        {
            throw new InvalidOperationException("Failed to sign data");
        }
        return Task.FromResult(new SignResult(sig.ToBytes(), SignatureHashType.SchnorrDefault));
    }

    public Task<MusigPartialSignature> SignMusig(
        MusigContext context,
        MusigPrivNonce nonce,
        CancellationToken cancellationToken = default)
    {
        var sig = context.Sign(_privateKey, nonce);
        return Task.FromResult(sig);
    }
}
