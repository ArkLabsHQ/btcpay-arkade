using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace BTCPayServer.Plugins.Arkade.Wallet;

public class NSecWalletSigner(ECPrivKey privateKey) : IArkadeWalletSigner
{
    private readonly ECPubKey _publicKey = privateKey.CreatePubKey();
    private readonly ECXOnlyPubKey _xOnlyPubKey = privateKey.CreateXOnlyPubKey();

    /// <summary>
    /// Creates a SingleKeySigningEntity from an nsec (Nostr-style bech32 private key).
    /// </summary>
    public static NSecWalletSigner FromNsec(string nsec)
    {
        var encoder2 = Bech32Encoder.ExtractEncoderFromString(nsec);
        encoder2.StrictLength = false;
        encoder2.SquashBytes = true;
        var keyData2 = encoder2.DecodeDataRaw(nsec, out _);
        var privKey = ECPrivKey.Create(keyData2);
        return new NSecWalletSigner(privKey);
    }
    
    public Task<MusigPartialSignature> SignMusig(OutputDescriptor descriptor, MusigContext context, MusigPrivNonce nonce,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(context.Sign(privateKey, nonce));
    }

    public Task<(ECXOnlyPubKey, SecpSchnorrSignature)> Sign(OutputDescriptor descriptor, uint256 hash, CancellationToken cancellationToken = default)
    {
        if (!descriptor.Extract().XOnlyPubKey.ToBytes().SequenceEqual(_publicKey.ToXOnlyPubKey().ToBytes()))
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