using System.Collections.Concurrent;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Services.Policies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Services;
using NArk.Services.Abstractions;

// Example usage:
// var policy = new FluentVtxoPolicy(logger)
//     .WithName("RefreshExpiring")
//     .WhenExpiringWithin(TimeSpan.FromHours(24))
//     .WhenRecoverable()
//     .RequireAll(); // Both conditions must be met
//
// intentScheduler.RegisterPolicy(walletId, policy);

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Background service that monitors VTXOs and schedules intents based on configured policies.
/// Used for refreshing expiring VTXOs, moving funds from recoverable state, and other automated VTXO management.
/// </summary>
public class ArkIntentScheduler(
    IServiceProvider serviceProvider,
    ArkIntentService intentService,
    ArkadeSpender arkadeSpender,
    ArkWalletService arkWalletService,
    ILogger<ArkIntentScheduler> logger,
    IOperatorTermsService operatorTermsService)
    : IHostedService, IDisposable
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, List<IVtxoIntentSchedulingPolicy>> _walletPolicies = new();
    private CancellationTokenSource? _serviceCts;
    private Task? _monitoringTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting ArkIntentScheduler");
        
        // Subscribe to wallet policy changes
        arkWalletService.WalletPolicyChanged += OnWalletPolicyChanged;
        
        // Load policies from database
        await LoadPoliciesFromDatabaseAsync(cancellationToken);
        
        _serviceCts = new CancellationTokenSource();
        _monitoringTask = MonitorAndScheduleIntentsAsync(_serviceCts.Token);
        
        logger.LogInformation("ArkIntentScheduler started");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping ArkIntentScheduler");
        
        // Unsubscribe from wallet policy changes
        arkWalletService.WalletPolicyChanged -= OnWalletPolicyChanged;
        
        if (_serviceCts != null)
            await _serviceCts.CancelAsync();
        
        if (_monitoringTask != null)
            await _monitoringTask;
        
        logger.LogInformation("ArkIntentScheduler stopped");
    }

    /// <summary>
    /// Register a policy for a specific wallet and save to database
    /// </summary>
    public async Task RegisterPolicyAsync(string walletId, IVtxoIntentSchedulingPolicy policy, CancellationToken cancellationToken = default)
    {
        var policies = _walletPolicies.GetOrAdd(walletId, _ => []);
        lock (policies)
        {
            policies.Add(policy);
        }
        
        await SavePoliciesToDatabaseAsync(walletId, cancellationToken);
        
        logger.LogInformation("Registered policy for wallet {WalletId}", walletId);
    }

    /// <summary>
    /// Register a policy for a specific wallet (in-memory only, not persisted)
    /// </summary>
    public void RegisterPolicy(string walletId, IVtxoIntentSchedulingPolicy policy)
    {
        var policies = _walletPolicies.GetOrAdd(walletId, _ => []);
        lock (policies)
        {
            policies.Add(policy);
        }
        
        logger.LogInformation("Registered policy for wallet {WalletId} (in-memory only)", walletId);
    }

    /// <summary>
    /// Unregister a specific policy instance for a wallet
    /// </summary>
    public void UnregisterPolicy(string walletId, IVtxoIntentSchedulingPolicy policy)
    {
        if (!_walletPolicies.TryGetValue(walletId, out var policies)) return;
        
        lock (policies)
        {
            policies.Remove(policy);
        }
            
        logger.LogInformation("Unregistered policy for wallet {WalletId}", walletId);
    }

    /// <summary>
    /// Clear all policies for a wallet
    /// </summary>
    public void ClearPolicies(string walletId)
    {
        _walletPolicies.TryRemove(walletId, out _);
        logger.LogInformation("Cleared all policies for wallet {WalletId}", walletId);
    }

    /// <summary>
    /// Get all registered policies for a wallet
    /// </summary>
    public IReadOnlyList<IVtxoIntentSchedulingPolicy> GetPolicies(string walletId)
    {
        if (_walletPolicies.TryGetValue(walletId, out var policies))
        {
            lock (policies)
            {
                return policies.ToList();
            }
        }
        
        return [];
    }

    private async Task MonitorAndScheduleIntentsAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting VTXO monitoring and intent scheduling loop");
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollingInterval, cancellationToken);
                
                // Get all wallets with policies
                var walletsWithPolicies = _walletPolicies.Keys.ToList();
                
                if (walletsWithPolicies.Count == 0)
                {
                    logger.LogDebug("No wallets with policies configured, skipping evaluation");
                    continue;
                }
                
                logger.LogDebug("Evaluating policies for {Count} wallets", walletsWithPolicies.Count);
                
                foreach (var walletId in walletsWithPolicies)
                {
                    try
                    {
                        await EvaluateWalletPoliciesAsync(walletId, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error evaluating policies for wallet {WalletId}", walletId);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in monitoring and intent scheduling loop");
            }
        }
        
        logger.LogInformation("VTXO monitoring and intent scheduling loop stopped");
    }

    private async Task EvaluateWalletPoliciesAsync(string walletId, CancellationToken cancellationToken)
    {
        // Get policies for this wallet, or use default policy
        List<IVtxoIntentSchedulingPolicy> policies;
        
        if (_walletPolicies.TryGetValue(walletId, out var customPolicies) && customPolicies.Count > 0)
        {
            policies = customPolicies;
        }
        else
        {
            // Use default policy for wallets without custom policies
            policies = [GetDefaultPolicy()];
            logger.LogDebug("Using default policy for wallet {WalletId}", walletId);
        }

        // Get spendable coins for this wallet
        var spendableCoinsDict = await arkadeSpender.GetSpendableCoins(
            [walletId], 
            true,
            cancellationToken);
        
        if (!spendableCoinsDict.TryGetValue(walletId, out var spendableCoins) || spendableCoins.Count == 0)
        {
            logger.LogDebug("No spendable coins found for wallet {WalletId}", walletId);
            return;
        }
        
        // Get wallet
        var wallet = await arkWalletService.GetWallet(walletId, cancellationToken);
        if (wallet == null)
        {
            logger.LogWarning("Wallet {WalletId} not found", walletId);
            return;
        }
        
        // Evaluate each policy
        List<IVtxoIntentSchedulingPolicy> policiesCopy;
        lock (policies)
        {
            policiesCopy = policies.ToList();
        }
        
        foreach (var policy in policiesCopy)
        {
            try
            {
                var intentSpec = await policy.EvaluateAsync(spendableCoins.ToArray(), cancellationToken);
                
                if (intentSpec != null)
                {
                    await CreateScheduledIntentAsync(wallet, intentSpec, cancellationToken);
                    
                    // Only create one intent per evaluation cycle
                    break;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error evaluating policy for wallet {WalletId}", walletId);
            }
        }
    }

    private async Task CreateScheduledIntentAsync(
        ArkWallet wallet, 
        ScheduledIntentSpec intentSpec,
        CancellationToken cancellationToken)
    {
        try
        {
            var terms = await operatorTermsService.GetOperatorTerms(cancellationToken);
            
            logger.LogInformation(
                "Creating scheduled intent for wallet {WalletId}: {Reason} with {Count} coins",
                wallet.Id, intentSpec.Reason, intentSpec.InputCoins.Length);
            
            var coins = intentSpec.InputCoins;
            var totalAmount = coins.Sum(c => c.Amount);
            // Use provided outputs or default to sending back to wallet
            IntentTxOut[] outputs;
            if (intentSpec.Outputs.Length > 0)
            {
                outputs = intentSpec.Outputs;
            }
            else
            {
                // Default: send all funds back to wallet (refreshes VTXOs, moves from recoverable state, etc.)
                var destination = await arkadeSpender.GetDestination(wallet, terms);

                var fees =
                    (terms.FeeTerms.OffchainInput * intentSpec.InputCoins.Length) +
                    terms.FeeTerms.OffchainOutput;
                
                outputs =
                [
                    new IntentTxOut
                    {
                        ScriptPubKey = destination.ScriptPubKey,
                        Type = IntentTxOut.IntentOutputType.VTXO,
                        Value = (ulong)totalAmount - (ulong)fees
                    }
                ];
            }            
            // Create the intent
            var intentId = await intentService.CreateIntentAsync(
                wallet.Id,
                coins,
                outputs,
                validFrom: intentSpec.ValidFrom,
                validUntil: intentSpec.ValidUntil,
                cancellationToken: cancellationToken);
            
            logger.LogInformation(
                "Created scheduled intent {IntentId} for wallet {WalletId} ({Reason}, {Count} coins, {Amount} sats)",
                intentId, wallet.Id, intentSpec.Reason, intentSpec.InputCoins.Length, totalAmount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, 
                "Failed to create scheduled intent for wallet {WalletId} ({Reason})",
                wallet.Id, intentSpec.Reason);
        }
    }

    private void OnWalletPolicyChanged(object? sender, string walletId)
    {
        logger.LogInformation("Wallet policy changed for {WalletId}, reloading policies", walletId);
        
        // Reload policies for this wallet asynchronously
        _ = Task.Run(async () =>
        {
            try
            {
                await LoadWalletPolicyAsync(walletId, CancellationToken.None);
                
                // Immediately evaluate the new policy
                await EvaluateWalletPoliciesAsync(walletId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to reload and evaluate policies for wallet {WalletId}", walletId);
            }
        });
    }

    private async Task LoadPoliciesFromDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Wait for wallet service to be ready and get all wallets with policies
            var wallets = await arkWalletService.GetWallets(cancellationToken);
            
            logger.LogInformation("Loading policies for {Count} wallets from wallet service", wallets.Length);
            
            foreach (var wallet in wallets)
            {
                LoadWalletPolicyFromWallet(wallet);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load policies from wallet service");
        }
    }

    private async Task LoadWalletPolicyAsync(string walletId, CancellationToken cancellationToken)
    {
        try
        {
            // Load wallet from service (uses cache)
            var wallet = await arkWalletService.GetWallet(walletId, cancellationToken);
            if (wallet == null)
            {
                logger.LogWarning("Wallet {WalletId} not found", walletId);
                _walletPolicies.TryRemove(walletId, out _);
                return;
            }
            
            LoadWalletPolicyFromWallet(wallet);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load policies for wallet {WalletId}", walletId);
        }
    }

    private void LoadWalletPolicyFromWallet(ArkWallet wallet)
    {
        try
        {
            if (string.IsNullOrEmpty(wallet.IntentSchedulingPolicy))
            {
                _walletPolicies[wallet.Id] = [GetDefaultPolicy()];
                logger.LogDebug("No policies configured for wallet {WalletId}, using default...", wallet.Id);
                return;
            }
            
            var configs = System.Text.Json.JsonSerializer.Deserialize<List<PolicyConfiguration>>(wallet.IntentSchedulingPolicy);
            if (configs == null || configs.Count == 0)
            {
                _walletPolicies[wallet.Id] = [GetDefaultPolicy()];
                logger.LogDebug("Corrupt policies configured for wallet {WalletId}, using default...", wallet.Id);
                return;
            }
            
            var policies =
                configs
                    .Select(config => FluentVtxoPolicy.FromConfiguration(serviceProvider, config))
                    .Cast<IVtxoIntentSchedulingPolicy>()
                    .ToList();

            _walletPolicies[wallet.Id] = policies;
            logger.LogInformation("Loaded {Count} policies for wallet {WalletId}", policies.Count, wallet.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load policies for wallet {WalletId}", wallet.Id);
        }
    }

    private async Task SavePoliciesToDatabaseAsync(string walletId, CancellationToken cancellationToken)
    {
        try
        {
            string? policyJson = null;
            
            if (_walletPolicies.TryGetValue(walletId, out var policies))
            {
                List<PolicyConfiguration> configs;
                lock (policies)
                {
                    configs = policies
                        .OfType<FluentVtxoPolicy>()
                        .Select(p => p.ToConfiguration())
                        .ToList();
                }
                
                if (configs.Count > 0)
                {
                    policyJson = System.Text.Json.JsonSerializer.Serialize(configs);
                }
            }
            
            await arkWalletService.UpdateWalletIntentSchedulingPolicy(walletId, policyJson, cancellationToken);
            logger.LogDebug("Saved policies for wallet {WalletId}", walletId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save policies for wallet {WalletId}", walletId);
        }
    }
    
    /// <summary>
    /// Get or create default policy for a wallet
    /// </summary>
    private IVtxoIntentSchedulingPolicy GetDefaultPolicy()
    {
        return ActivatorUtilities.CreateInstance<FluentVtxoPolicy>(serviceProvider)
            .WhenExpiringWithin(TimeSpan.FromMinutes(5))
            .WhenRecoverable()
            .WithReason("Auto-refresh expiring or recoverable coins")
            .WithValidityWindow(TimeSpan.FromHours(2));
    }

    public void Dispose()
    {
        _serviceCts?.Cancel();
        _serviceCts?.Dispose();
        _walletPolicies.Clear();
    }
}
