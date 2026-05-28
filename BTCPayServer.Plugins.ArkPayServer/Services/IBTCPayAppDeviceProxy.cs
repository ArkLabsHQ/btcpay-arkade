using NArk.Abstractions.Wallets;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Cross-plugin marker that the BTCPayServer.Plugins.App companion plugin
/// implements to bridge <see cref="IRemoteSignerTransport"/> calls to a
/// connected BTCPayApp device over its SignalR hub.
/// </summary>
/// <remarks>
/// Inherits the four <see cref="IRemoteSignerTransport"/> methods unchanged
/// — the App plugin implements them by forwarding the call to the device
/// pairing matching <c>walletId</c>. Carving this out as a distinct interface
/// gives the App-plugin impl a stable type to bind to, and lets this plugin
/// resolve the transport with a soft fallback: if no
/// <see cref="IBTCPayAppDeviceProxy"/> is registered, the plugin still loads
/// and watch-only wallets remain usable read-only — signing-dependent
/// operations fail with a clear "no remote signer registered" message,
/// scoped to the moment a signer is actually requested rather than at
/// container build time.
/// </remarks>
public interface IBTCPayAppDeviceProxy : IRemoteSignerTransport
{
}
