using NArk.Extensions;
using NArk.Services.Abstractions;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace NArk.Services;

public class MemoryNsecWalletSigner : IArkadeWalletSigner
{
    private readonly ECPrivKey _key;

    public MemoryNsecWalletSigner(ECPrivKey key)
    {
        _key = key;
    }


    public Task<string> GetFingerprint(CancellationToken cancellationToken)
    {
        return Task.FromResult(_key.CreatePubKey().ToHex());
    }

    public Task<(SecpSchnorrSignature, ECXOnlyPubKey)> Sign(uint256 data, OutputDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        if (!descriptor.ToXOnlyPubKey().ToBytes().SequenceEqual(_key.CreateXOnlyPubKey().ToBytes()))
        {
            throw new Exception("invalid descriptor, cannot sign");
        }

        var sig = _key.SignBIP340(data.ToBytes());
        return Task.FromResult((sig, _key.CreateXOnlyPubKey()));
    }

    public Task<MusigPartialSignature> SignMusig(MusigContext context, OutputDescriptor descriptor,
        MusigPrivNonce nonce,
        CancellationToken cancellationToken = default)
    {
        if (!descriptor.ToXOnlyPubKey().ToBytes().SequenceEqual(_key.CreateXOnlyPubKey().ToBytes()))
        {
            throw new Exception("invalid descriptor, cannot sign");
        }

        // Create MUSIG2 partial signature using the private key and nonce
        var partialSig = context.Sign(_key, nonce);
        return Task.FromResult(partialSig);
    }
}

public class MemorySeedWalletSigner : IArkadeWalletSigner
{
    private readonly ExtKey _seed;

    public MemorySeedWalletSigner(Mnemonic mnemonic, string passphrase = null)
    {
        _seed = mnemonic.DeriveExtKey(passphrase);
    }


    public async Task<string> GetFingerprint(CancellationToken cancellationToken)
    {
        return _seed.GetPublicKey().GetHDFingerPrint().ToString();
    }

    public async Task<(SecpSchnorrSignature, ECXOnlyPubKey)> Sign(uint256 data, OutputDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        var key = await DerivePrivateKey(descriptor, cancellationToken);
        var sig = key.SignBIP340(data.ToBytes());
        return (sig, key.CreateXOnlyPubKey());
    }

    public async Task<MusigPartialSignature> SignMusig(MusigContext context, OutputDescriptor descriptor,
        MusigPrivNonce nonce,
        CancellationToken cancellationToken = default)
    {
        // Create MUSIG2 partial signature using the private key and nonce
        return context.Sign(await DerivePrivateKey(descriptor, cancellationToken), nonce);
    }


    private async Task<ECPrivKey> DerivePrivateKey(OutputDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (descriptor.WalletId() != await GetFingerprint(cancellationToken))
        {
            throw new Exception("invalid descriptor, cannot sign");
        }

        var info = descriptor.Extract();

        return _seed.Derive(info.fullPath!).PrivateKey.ToKey();
    }
}