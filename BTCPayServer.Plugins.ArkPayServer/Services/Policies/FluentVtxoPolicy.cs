using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NArk.Services;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using NArk;
using NArk.Contracts;
using NArk.Services.Abstractions;

namespace BTCPayServer.Plugins.ArkPayServer.Services.Policies;

#region Serialization Models

/// <summary>
/// Serializable policy configuration
/// </summary>
public class PolicyConfiguration
{
    [JsonPropertyName("requireAll")]
    public bool RequireAll { get; set; } = false;

    [JsonPropertyName("conditions")]
    public List<PolicyCondition> Conditions { get; set; } = new();
}

/// <summary>
/// Represents a single policy condition
/// </summary>
public class PolicyCondition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public Dictionary<string, object> Parameters { get; set; } = new();
}

/// <summary>
/// Known condition types
/// </summary>
public static class PolicyConditionTypes
{
    public const string ExpiringWithin = "ExpiringWithin";
    public const string Recoverable = "Recoverable";
    public const string RecoverableFundsExceed = "RecoverableFundsExceed";
    public const string TotalValueExceeds = "TotalValueExceeds";
    public const string CountExceeds = "CountExceeds";
    public const string ScriptMatches = "ScriptMatches";
}

#endregion

/// <summary>
/// Fluent policy builder that allows chaining multiple conditions for scheduling intents
/// </summary>
public class FluentVtxoPolicy(ILogger<FluentVtxoPolicy> logger) : IVtxoIntentSchedulingPolicy
{
    // Readonly but mutable
    private readonly List<Func<SpendableArkCoinWithSigner[], CancellationToken, Task<SpendableArkCoinWithSigner[]>>> _filters = [];
    private readonly List<PolicyCondition> _serializableConditions = [];
    
    private bool _requireAll = false;
    private Func<SpendableArkCoinWithSigner[], CancellationToken, Task<IntentTxOut[]>>? _outputBuilder;
    private TimeSpan _validityWindow = TimeSpan.FromHours(1);
    private string? _reason;

    /// <summary>
    /// Create a policy from a serialized configuration
    /// </summary>
    public static FluentVtxoPolicy FromConfiguration(IServiceProvider serviceProvider, PolicyConfiguration config)
    {
        var policy = ActivatorUtilities.CreateInstance<FluentVtxoPolicy>(serviceProvider);

        if (config.RequireAll)
        {
            policy.RequireAll();
        }

        foreach (var condition in config.Conditions)
        {
            policy.AddConditionFromConfig(condition);
        }

        return policy;
    }

    /// <summary>
    /// Serialize this policy to a configuration object
    /// </summary>
    public PolicyConfiguration ToConfiguration()
    {
        return new PolicyConfiguration
        {
            RequireAll = _requireAll,
            Conditions = _serializableConditions.ToList()
        };
    }

    /// <summary>
    /// Serialize this policy to JSON
    /// </summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(ToConfiguration(), new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Deserialize a policy from JSON
    /// </summary>
    public static FluentVtxoPolicy FromJson(IServiceProvider serviceProvider, string json)
    {
        var config = JsonSerializer.Deserialize<PolicyConfiguration>(json);
        if (config == null)
        {
            throw new InvalidOperationException("Failed to deserialize policy configuration");
        }
        return FromConfiguration(serviceProvider, config);
    }

    private void AddConditionFromConfig(PolicyCondition condition)
    {
        switch (condition.Type)
        {
            case PolicyConditionTypes.ExpiringWithin:
                if (condition.Parameters.TryGetValue("hours", out var hoursObj) && hoursObj is JsonElement hoursEl)
                {
                    WhenExpiringWithin(TimeSpan.FromHours(hoursEl.GetDouble()));
                }
                break;

            case PolicyConditionTypes.Recoverable:
                WhenRecoverable();
                break;

            case PolicyConditionTypes.RecoverableFundsExceed:
                if (condition.Parameters.TryGetValue("satoshis", out var recSatsObj) && recSatsObj is JsonElement recSatsEl)
                {
                    WhenRecoverableFundsExceed(Money.Satoshis(recSatsEl.GetInt64()));
                }
                break;

            case PolicyConditionTypes.TotalValueExceeds:
                if (condition.Parameters.TryGetValue("satoshis", out var totalSatsObj) && totalSatsObj is JsonElement totalSatsEl)
                {
                    WhenTotalValueExceeds(Money.Satoshis(totalSatsEl.GetInt64()));
                }
                break;

            case PolicyConditionTypes.CountExceeds:
                if (condition.Parameters.TryGetValue("count", out var countObj) && countObj is JsonElement countEl)
                {
                    WhenCountExceeds(countEl.GetInt32());
                }
                break;

            case PolicyConditionTypes.ScriptMatches:
                if (condition.Parameters.TryGetValue("patterns", out var patternsObj) && patternsObj is JsonElement patternsEl)
                {
                    var patterns = patternsEl.EnumerateArray().Select(e => e.GetString()!).ToArray();
                    WhenScriptMatches(patterns);
                }
                break;

            default:
                logger.LogWarning("Unknown policy condition type: {Type}", condition.Type);
                break;
        }
    }

    /// <summary>
    /// Require all conditions to match (AND logic). Default is OR logic.
    /// </summary>
    public FluentVtxoPolicy RequireAll()
    {
        _requireAll = true;
        return this;
    }

    /// <summary>
    /// Filter coins that are expiring within the specified timespan
    /// </summary>
    public FluentVtxoPolicy WhenExpiringWithin(TimeSpan threshold)
    {
        _serializableConditions.Add(new PolicyCondition
        {
            Type = PolicyConditionTypes.ExpiringWithin,
            Parameters = new Dictionary<string, object> { ["hours"] = threshold.TotalHours }
        });

        _filters.Add(( coins, ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var thresholdDate = now.Add(threshold);

            var filtered = coins
                .Where(c => c.SpendingLockTime == null || c.SpendingLockTime.Value <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                .Where(c => c.Contract is ArkPaymentContract payment && 
                            c.ExpiresAt <= thresholdDate && c.ExpiresAt > now)
                .ToArray();

            if (filtered.Any())
            {
                logger.LogDebug("Filter WhenExpiringWithin({Hours}h): {Count} coins",
                    threshold.TotalHours, filtered.Length);
            }

            return Task.FromResult(filtered);
        });
        return this;
    }

    /// <summary>
    /// Filter coins that are in recoverable state (note contracts)
    /// </summary>
    public FluentVtxoPolicy WhenRecoverable()
    {
        _serializableConditions.Add(new PolicyCondition
        {
            Type = PolicyConditionTypes.Recoverable,
            Parameters = new Dictionary<string, object>()
        });

        _filters.Add((coins, ct) =>
        {
            var filtered = coins
                .Where(c => c.Contract is ArkNoteContract)
                .ToArray();

            if (filtered.Any())
            {
                logger.LogDebug("Filter WhenRecoverable: {Count} coins",
                    filtered.Length);
            }

            return Task.FromResult(filtered);
        });
        return this;
    }

    /// <summary>
    /// Filter coins when their total value exceeds threshold
    /// </summary>
    public FluentVtxoPolicy WhenTotalValueExceeds(Money threshold)
    {
        _serializableConditions.Add(new PolicyCondition
        {
            Type = PolicyConditionTypes.TotalValueExceeds,
            Parameters = new Dictionary<string, object> { ["satoshis"] = threshold.Satoshi }
        });

        _filters.Add((coins, ct) =>
        {
            var total = coins.Sum(c => c.Amount);

            if (total >= threshold)
            {
                logger.LogDebug("Filter WhenTotalValueExceeds({Threshold}): {Total} sats",
                    threshold.Satoshi, total);
                return Task.FromResult(coins);
            }

            return Task.FromResult(Array.Empty<SpendableArkCoinWithSigner>());
        });
        return this;
    }

    /// <summary>
    /// Filter coins when recoverable funds exceed threshold
    /// </summary>
    public FluentVtxoPolicy WhenRecoverableFundsExceed(Money threshold)
    {
        _serializableConditions.Add(new PolicyCondition
        {
            Type = PolicyConditionTypes.RecoverableFundsExceed,
            Parameters = new Dictionary<string, object> { ["satoshis"] = threshold.Satoshi }
        });

        _filters.Add((coins, ct) =>
        {
            var recoverable = coins
                .Where(c => c.Contract is ArkNoteContract)
                .ToArray();

            var total = recoverable.Sum(c => c.Amount);

            if (total >= threshold)
            {
                logger.LogDebug("Filter WhenRecoverableFundsExceed({Threshold}): {Total} sats",
                    threshold.Satoshi, total);
                return Task.FromResult(recoverable);
            }

            return Task.FromResult(Array.Empty<SpendableArkCoinWithSigner>());
        });
        return this;
    }

    /// <summary>
    /// Filter coins when their count exceeds threshold
    /// </summary>
    public FluentVtxoPolicy WhenCountExceeds(int maxCount)
    {
        _serializableConditions.Add(new PolicyCondition
        {
            Type = PolicyConditionTypes.CountExceeds,
            Parameters = new Dictionary<string, object> { ["count"] = maxCount }
        });

        _filters.Add((coins, ct) =>
        {
            if (coins.Length >= maxCount)
            {
                logger.LogDebug("Filter WhenCountExceeds({MaxCount}): {Count} coins",
                    maxCount, coins.Length);
                return Task.FromResult(coins);
            }

            return Task.FromResult(Array.Empty<SpendableArkCoinWithSigner>());
        });
        return this;
    }

    /// <summary>
    /// Filter coins matching specific script patterns
    /// </summary>
    public FluentVtxoPolicy WhenScriptMatches(params string[] patterns)
    {
        _serializableConditions.Add(new PolicyCondition
        {
            Type = PolicyConditionTypes.ScriptMatches,
            Parameters = new Dictionary<string, object> { ["patterns"] = patterns }
        });

        _filters.Add((coins, ct) =>
        {
            var filtered = coins
                .Where(c => patterns.Any(p => c.TxOut.ScriptPubKey.ToHex().Contains(p, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            if (filtered.Any())
            {
                logger.LogDebug("Filter WhenScriptMatches: {Count} coins",
                    filtered.Length);
            }

            return Task.FromResult(filtered);
        });
        return this;
    }

    /// <summary>
    /// Filter coins with a custom predicate
    /// </summary>
    public FluentVtxoPolicy When(Func<SpendableArkCoinWithSigner, bool> predicate, string? description = null)
    {
        _filters.Add((coins, ct) =>
        {
            var filtered = coins
                .Where(predicate)
                .ToArray();

            if (filtered.Any())
            {
                logger.LogDebug("Filter When({Description}): {Count} coins",
                    description ?? "custom", filtered.Length);
            }

            return Task.FromResult(filtered);
        });
        return this;
    }

    /// <summary>
    /// Filter coins with a custom async filter
    /// </summary>
    public FluentVtxoPolicy WhenAsync(Func<SpendableArkCoinWithSigner[], CancellationToken, Task<SpendableArkCoinWithSigner[]>> filter)
    {
        _filters.Add(filter);
        return this;
    }

    /// <summary>
    /// Set custom output builder for the intent (e.g., for onchain withdrawals)
    /// </summary>
    public FluentVtxoPolicy WithOutputs(Func<SpendableArkCoinWithSigner[], CancellationToken, Task<IntentTxOut[]>> outputBuilder)
    {
        _outputBuilder = outputBuilder;
        return this;
    }

    /// <summary>
    /// Set custom outputs for the intent
    /// </summary>
    public FluentVtxoPolicy WithOutputs(params IntentTxOut[] outputs)
    {
        _outputBuilder = ( _, _) => Task.FromResult(outputs);
        return this;
    }

    /// <summary>
    /// Set the validity window for the intent
    /// </summary>
    public FluentVtxoPolicy WithValidityWindow(TimeSpan window)
    {
        _validityWindow = window;
        return this;
    }

    /// <summary>
    /// Set a reason/description for this intent
    /// </summary>
    public FluentVtxoPolicy WithReason(string reason)
    {
        _reason = reason;
        return this;
    }

    public async Task<ScheduledIntentSpec?> EvaluateAsync(SpendableArkCoinWithSigner[] coins, CancellationToken cancellationToken = default)
    {
        if (_filters.Count == 0)
        {
            return null;
        }

        SpendableArkCoinWithSigner[] matchedCoins;

        if (_requireAll)
        {
            // AND logic: apply filters sequentially, each narrowing the set
            var current = coins;
            foreach (var filter in _filters)
            {
                current = await filter(current, cancellationToken);
                if (current.Length == 0)
                {
                    return null;
                }
            }

            matchedCoins = current;
        }
        else
        {
            // OR logic: collect results from all filters and union them
            var allResults = new HashSet<SpendableArkCoinWithSigner>();
            foreach (var filter in _filters)
            {
                var result = await filter(coins, cancellationToken);
                foreach (var coin in result)
                {
                    allResults.Add(coin);
                }
            }

            matchedCoins = allResults.ToArray();
        }

        if (matchedCoins.Length == 0)
        {
            return null;
        }

        logger.LogInformation(
            "Policy triggered: {Count} coins",
            matchedCoins.Length);

        // Build outputs - use custom builder if provided, otherwise empty (scheduler will default to wallet)
        var outputs = _outputBuilder is not null
            ? await _outputBuilder(matchedCoins, cancellationToken)
            : [];

        var now = DateTimeOffset.UtcNow;
        return new ScheduledIntentSpec
        {
            InputCoins = matchedCoins,
            Outputs = outputs,
            ValidFrom = now,
            ValidUntil = now.Add(_validityWindow),
            Reason = _reason ?? "Policy triggered"
        };
    }
}
