namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>The wallet's spend capability from the connection string's <c>spend-key</c>; absent means receive-only.</summary>
// A dedicated type because ActivatorUtilities binds by type, and a second string beside the wallet id could swap.
public sealed record ArkLightningSpendCapability(string? Value);
