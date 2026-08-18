namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>
/// Capability authorising spend operations on an Arkade wallet through the Lightning
/// connection string.
///
/// Issued to the store that generates a wallet and presented back via the connection
/// string's <c>spend-key</c>. Stores that reference a wallet by id get a receive-only
/// client.
///
/// Wrapped in a dedicated type rather than passed as a bare <see cref="string"/> because
/// <c>ActivatorUtilities.CreateInstance</c> binds constructor arguments by type — a second
/// <see cref="string"/> parameter alongside the wallet id could bind in either order.
/// </summary>
/// <param name="Value">The capability, or <c>null</c> when the caller presented none.</param>
public sealed record ArkLightningSpendCapability(string? Value);
