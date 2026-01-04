using NArk.Abstractions.Wallets;
using NBitcoin;
using NBitcoin.DataEncoders;
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
        var pubKeyHex = Convert.ToHexString(_xOnlyPubKey.ToBytes()).ToLowerInvariant();
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
        // nsec1... is bech32 encoding (not bech32m), with 5-bit groups
        // Decode using NBitcoin's Bech32 facilities
        var encoder = Encoders.Bech32("nsec");
        // The DecodeDataRaw returns 5-bit groups, need to convert to 8-bit bytes
        var decoded = encoder.DecodeDataRaw(nsec, out var encodingType);
        // Convert from 5-bit to 8-bit
        var bytes = ConvertBits(decoded, 5, 8, false);
        var privKey = ECPrivKey.Create(bytes);
        return new SingleKeySigningEntity(privKey, network);
    }

    private static byte[] ConvertBits(byte[] data, int fromBits, int toBits, bool pad)
    {
        var acc = 0;
        var bits = 0;
        var result = new List<byte>();
        var maxv = (1 << toBits) - 1;
        foreach (var value in data)
        {
            acc = (acc << fromBits) | value;
            bits += fromBits;
            while (bits >= toBits)
            {
                bits -= toBits;
                result.Add((byte)((acc >> bits) & maxv));
            }
        }
        if (pad && bits > 0)
        {
            result.Add((byte)((acc << (toBits - bits)) & maxv));
        }
        return result.ToArray();
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
            ["pubkey"] = Convert.ToHexString(_xOnlyPubKey.ToBytes()).ToLowerInvariant()
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
        return Task.FromResult(new SignResult(sig, _xOnlyPubKey));
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
