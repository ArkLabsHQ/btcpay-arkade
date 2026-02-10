using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Wallets;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace BTCPayServer.Plugins.ArkPayServer.Wallet;

public class HierarchicalDeterministicWalletSigner(ArkWallet wallet) : IArkadeWalletSigner
{
    private Task<ECPrivKey> DerivePrivateKey(OutputDescriptor descriptor)
    {
        var fullPath = descriptor.Extract().FullPath ?? throw new InvalidOperationException();
        var mnemonic = new Mnemonic(wallet.Wallet);
        var extKey = mnemonic.DeriveExtKey();
        return Task.FromResult(ECPrivKey.Create(extKey.Derive(fullPath).PrivateKey.ToBytes()));
    }
    
    public async Task<MusigPartialSignature> SignMusig(OutputDescriptor descriptor, MusigContext context, MusigPrivNonce nonce,
        CancellationToken cancellationToken = default)
    {
        var privKey = await DerivePrivateKey(descriptor);
        return context.Sign(privKey, nonce);
    }

    public async Task<(ECXOnlyPubKey, SecpSchnorrSignature)> Sign(OutputDescriptor descriptor, uint256 hash, CancellationToken cancellationToken = default)
    {
        var privKey = await DerivePrivateKey(descriptor);
        return (privKey.CreateXOnlyPubKey(), privKey.SignBIP340(hash.ToBytes()));
    }

    public async Task<MusigPrivNonce> GenerateNonces(OutputDescriptor descriptor, MusigContext context, CancellationToken cancellationToken = default)
    {
        var privKey = await DerivePrivateKey(descriptor);
        return context.GenerateNonce(privKey);
    }
}