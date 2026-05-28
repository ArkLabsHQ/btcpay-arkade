using NArk.Abstractions.Wallets;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Sentinel <see cref="IRemoteSignerTransport"/> used when no
/// <see cref="IBTCPayAppDeviceProxy"/> is registered. Every signing
/// method throws a clear "install the App companion plugin" error, so
/// the failure surfaces exactly at the moment a <see cref="WalletType.Remote"/>
/// wallet tries to sign — not at container build time. Pure
/// <see cref="WalletType.WatchOnly"/> wallets never reach this code path,
/// because NArk's <c>DefaultWalletProvider.GetSignerAsync</c> only
/// materializes a <c>RemoteArkadeWalletSigner</c> over the transport for
/// <c>WalletType.Remote</c>.
/// </summary>
internal sealed class MissingDeviceProxyTransport : IRemoteSignerTransport
{
    private const string ErrorMessage =
        "No IBTCPayAppDeviceProxy is registered. " +
        "Install the BTCPayServer.Plugins.App companion plugin and pair a BTCPayApp device " +
        "to enable remote signing for watch-only/remote wallets.";

    public Task<ECPubKey> GetPubKeyAsync(
        string walletId,
        OutputDescriptor descriptor,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(ErrorMessage);

    public Task<MusigPartialSignature> SignMusigAsync(
        string walletId,
        OutputDescriptor descriptor,
        MusigContext context,
        MusigPrivNonce nonce,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(ErrorMessage);

    public Task<(ECXOnlyPubKey, SecpSchnorrSignature)> SignAsync(
        string walletId,
        OutputDescriptor descriptor,
        uint256 hash,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(ErrorMessage);

    public Task<MusigPrivNonce> GenerateNoncesAsync(
        string walletId,
        OutputDescriptor descriptor,
        MusigContext context,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(ErrorMessage);
}
