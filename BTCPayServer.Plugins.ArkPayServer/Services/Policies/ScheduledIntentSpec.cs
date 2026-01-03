using NArk;
using NArk.Abstractions;
using NArk.Transactions;

namespace BTCPayServer.Plugins.ArkPayServer.Services.Policies;

/// <summary>
/// Specification for a scheduled intent returned by a policy
/// </summary>
public class ScheduledIntentSpec
{
    /// <summary>
    /// Spendable coins to use in the intent
    /// </summary>
    public required ArkPsbtSigner[] InputCoins { get; init; }

    /// <summary>
    /// Outputs for the intent (can be Ark addresses or onchain addresses)
    /// </summary>
    public required ArkTxOut[] Outputs { get; init; }

    /// <summary>
    /// When the intent becomes valid
    /// </summary>
    public DateTimeOffset ValidFrom { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the intent expires
    /// </summary>
    public DateTimeOffset ValidUntil { get; init; } = DateTimeOffset.UtcNow.AddHours(1);

    /// <summary>
    /// Optional reason/description for this intent
    /// </summary>
    public string? Reason { get; init; }
}
