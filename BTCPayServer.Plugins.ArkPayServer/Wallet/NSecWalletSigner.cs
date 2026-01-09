using NArk.Abstractions.Wallets;
using NArk.Swaps.Helpers;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace BTCPayServer.Plugins.ArkPayServer.Wallet;

public class NSecWalletSigner(ECPrivKey privateKey) : IArkadeWalletSigner
{
    private readonly ECPubKey _publicKey = privateKey.CreatePubKey();
    private readonly ECXOnlyPubKey _xOnlyPubKey = privateKey.CreateXOnlyPubKey();

    /// <summary>
    /// Creates a SingleKeySigningEntity from an nsec (Nostr-style bech32 private key).
    /// </summary>
    public static NSecWalletSigner FromNsec(string nsec)
    {
        // nsec1... is bech32 encoding (not bech32m), with 5-bit groups
        // Decode using NBitcoin's Bech32 facilities
        var encoder = Encoders.Bech32("nsec");
        // The DecodeDataRaw returns 5-bit groups, need to convert to 8-bit bytes
        var decoded = encoder.DecodeDataRaw(nsec, out var encodingType);
        // Convert from 5-bit to 8-bit
        var bytes = ConvertBits(decoded, 5, 8, false);
        var privKey = ECPrivKey.Create(bytes);
        return new NSecWalletSigner(privKey);
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
    
    public Task<MusigPartialSignature> SignMusig(OutputDescriptor descriptor, MusigContext context, MusigPrivNonce nonce,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(context.Sign(privateKey, nonce));
    }

    public Task<(ECXOnlyPubKey, SecpSchnorrSignature)> Sign(OutputDescriptor descriptor, uint256 hash, CancellationToken cancellationToken = default)
    {
        if (descriptor.Extract().PubKey! != _publicKey)
            throw new InvalidOperationException("Descriptor does not belong to this wallet");
        
        if (!privateKey.TrySignBIP340(hash.ToBytes(), null, out var sig))
        {
            throw new InvalidOperationException("Failed to sign data");
        }
        
        return Task.FromResult((_xOnlyPubKey, sig));
    }

    public Task<MusigPrivNonce> GenerateNonces(OutputDescriptor descriptor, MusigContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(context.GenerateNonce(privateKey));
    }
}