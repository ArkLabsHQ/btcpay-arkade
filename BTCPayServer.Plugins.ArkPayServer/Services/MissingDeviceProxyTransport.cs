using NArk.Abstractions.Wallets;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using NBitcoin.Secp256k1.Musig;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Sentinel <see cref="IRemoteSignerTransport"/> used when no
/// <see cref="IBTCPayAppDeviceProxy"/> is registered. <see cref="KnowsWalletAsync"/>
/// returns <c>false</c> for every wallet so NArk's <c>DefaultWalletProvider</c>
/// never wires it up as a signing source — a wallet imported via the watch-only
/// flow stays watch-only end-to-end (Send/sign operations surface a generic
/// "no signing material" failure at the call site).
/// </summary>
/// <remarks>
/// The three signing methods still throw a clear "install the App companion
/// plugin" message as defence-in-depth, in case a future <c>DefaultWalletProvider</c>
/// change or a third-party caller routes a sign request through this sentinel
/// without consulting <see cref="KnowsWalletAsync"/> first.
/// </remarks>
internal sealed class MissingDeviceProxyTransport : IRemoteSignerTransport
{
    private const string ErrorMessage =
        "No IBTCPayAppDeviceProxy is registered. " +
        "Install the BTCPayServer.Plugins.App companion plugin and pair a BTCPayApp device " +
        "to enable remote signing for watch-only wallets.";

    public Task<bool> KnowsWalletAsync(
        string walletId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<ECPubKey> GetPubKeyAsync(
        string walletId,
        OutputDescriptor descriptor,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(ErrorMessage);

    public Task<MusigPartialSignature> SignMusigAsync(
        string walletId,
        OutputDescriptor descriptor,
        MusigContext context,
        string sessionId,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(ErrorMessage);

    public Task<(ECXOnlyPubKey, SecpSchnorrSignature)> SignAsync(
        string walletId,
        OutputDescriptor descriptor,
        uint256 hash,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(ErrorMessage);

    public Task<MusigPubNonce> GenerateNoncesAsync(
        string walletId,
        OutputDescriptor descriptor,
        MusigContext context,
        string sessionId,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(ErrorMessage);
}
