using NArk.Abstractions.Wallets;
using NArk.Swaps.Helpers;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace BTCPayServer.Plugins.ArkPayServer.Wallet;

public class NSecAddressProvider(
    OutputDescriptor outputDescriptor,
    string fingerprint
) : IArkadeAddressProvider
{
    /// <summary>
    /// Creates a SingleKeySigningEntity from an nsec (Nostr-style bech32 private key).
    /// </summary>
    public static NSecAddressProvider FromNsec(string nsec, Network network)
    {
        // nsec1... is bech32 encoding (not bech32m), with 5-bit groups
        // Decode using NBitcoin's Bech32 facilities
        var encoder = Encoders.Bech32("nsec");
        // The DecodeDataRaw returns 5-bit groups, need to convert to 8-bit bytes
        var decoded = encoder.DecodeDataRaw(nsec, out var encodingType);
        // Convert from 5-bit to 8-bit
        var bytes = ConvertBits(decoded, 5, 8, false);
        
        var privKey = ECPrivKey.Create(bytes);
        var xOnlyPubKey = privKey.CreateXOnlyPubKey();
        // Create a simple taproot descriptor: tr(pubkey_hex)
        var pubKeyHex = Convert.ToHexString(xOnlyPubKey.ToBytes()).ToLowerInvariant();
        var outputDescriptor = OutputDescriptor.Parse($"tr({pubKeyHex})", network);

        // For single-key wallets, the fingerprint is the pubkey hex
        var fingerprint = pubKeyHex;

        return new NSecAddressProvider(outputDescriptor, fingerprint);
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

    public async Task<string> GetWalletFingerprint(CancellationToken cancellationToken = default)
    {
        return fingerprint;
    }

    public async Task<ECPubKey> GetPublicKey(CancellationToken cancellationToken = default) =>
        outputDescriptor.Extract().PubKey!;

    public Task<OutputDescriptor> GetNewSigningDescriptor(string identifier, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(outputDescriptor);
    }
}