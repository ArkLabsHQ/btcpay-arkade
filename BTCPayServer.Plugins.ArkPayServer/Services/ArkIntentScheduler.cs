using System.Collections.Concurrent;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Services.Policies;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Services;
using NArk.Transport;

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
public class ArkIntentScheduler : IHostedService, IDisposable
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMinutes(5);
    
    private readonly ArkIntentService _intentService;
    private readonly ArkadeSpender _arkadeSpender;
    private readonly ArkWalletService _arkWalletService;
    private readonly ILogger<ArkIntentScheduler> _logger;
    private readonly ILogger<FluentVtxoPolicy> _policyLogger;
    private readonly IClientTransport _clientTransport;

    private readonly ConcurrentDictionary<string, List<IVtxoIntentSchedulingPolicy>> _walletPolicies = new();
    private CancellationTokenSource? _serviceCts;
    private Task? _monitoringTask;

    public ArkIntentScheduler(
        ArkIntentService intentService,
        ArkadeSpender arkadeSpender,
        ArkWalletService arkWalletService,
        ILogger<ArkIntentScheduler> logger,
        ILogger<FluentVtxoPolicy> policyLogger,
        IClientTransport clientTransport)
    {
        _intentService = intentService;
        _arkadeSpender = arkadeSpender;
        _arkWalletService = arkWalletService;
        _logger = logger;
        _policyLogger = policyLogger;
        _clientTransport = clientTransport;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting ArkIntentScheduler");
        
        // Subscribe to wallet policy changes
        _arkWalletService.WalletPolicyChanged += OnWalletPolicyChanged;
        
        // Load policies from database
        await LoadPoliciesFromDatabaseAsync(cancellationToken);
        
        // Register default policies for all wallets
        await RegisterDefaultPoliciesAsync(cancellationToken);
        
        _serviceCts = new CancellationTokenSource();
        _monitoringTask = MonitorAndScheduleIntentsAsync(_serviceCts.Token);
        
        _logger.LogInformation("ArkIntentScheduler started");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping ArkIntentScheduler");
        
        // Unsubscribe from wallet policy changes
        _arkWalletService.WalletPolicyChanged -= OnWalletPolicyChanged;
        
        if (_serviceCts != null)
            await _serviceCts.CancelAsync();
        
        if (_monitoringTask != null)
            await _monitoringTask;
        
        _logger.LogInformation("ArkIntentScheduler stopped");
    }

    /// <summary>
    /// Register a policy for a specific wallet and save to database
    /// </summary>
    public async Task RegisterPolicyAsync(string walletId, IVtxoIntentSchedulingPolicy policy, CancellationToken cancellationToken = default)
    {
        var policies = _walletPolicies.GetOrAdd(walletId, _ => new List<IVtxoIntentSchedulingPolicy>());
        lock (policies)
        {
            policies.Add(policy);
        }
        
        await SavePoliciesToDatabaseAsync(walletId, cancellationToken);
        
        _logger.LogInformation("Registered policy for wallet {WalletId}", walletId);
    }

    /// <summary>
    /// Register a policy for a specific wallet (in-memory only, not persisted)
    /// </summary>
    public void RegisterPolicy(string walletId, IVtxoIntentSchedulingPolicy policy)
    {
        var policies = _walletPolicies.GetOrAdd(walletId, _ => new List<IVtxoIntentSchedulingPolicy>());
        lock (policies)
        {
            policies.Add(policy);
        }
        
        _logger.LogInformation("Registered policy for wallet {WalletId} (in-memory only)", walletId);
    }

    /// <summary>
    /// Unregister a specific policy instance for a wallet
    /// </summary>
    public void UnregisterPolicy(string walletId, IVtxoIntentSchedulingPolicy policy)
    {
        if (_walletPolicies.TryGetValue(walletId, out var policies))
        {
            lock (policies)
            {
                policies.Remove(policy);
            }
            
            _logger.LogInformation("Unregistered policy for wallet {WalletId}", walletId);
        }
    }

    /// <summary>
    /// Clear all policies for a wallet
    /// </summary>
    public void ClearPolicies(string walletId)
    {
        _walletPolicies.TryRemove(walletId, out _);
        _logger.LogInformation("Cleared all policies for wallet {WalletId}", walletId);
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
        
        return Array.Empty<IVtxoIntentSchedulingPolicy>();
    }

    private async Task MonitorAndScheduleIntentsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting VTXO monitoring and intent scheduling loop");
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollingInterval, cancellationToken);
                
                // Get all wallets with policies
                var walletsWithPolicies = _walletPolicies.Keys.ToList();
                
                if (walletsWithPolicies.Count == 0)
                {
                    _logger.LogDebug("No wallets with policies configured, skipping evaluation");
                    continue;
                }
                
                _logger.LogDebug("Evaluating policies for {Count} wallets", walletsWithPolicies.Count);
                
                foreach (var walletId in walletsWithPolicies)
                {
                    try
                    {
                        await EvaluateWalletPoliciesAsync(walletId, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error evaluating policies for wallet {WalletId}", walletId);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in monitoring and intent scheduling loop");
            }
        }
        
        _logger.LogInformation("VTXO monitoring and intent scheduling loop stopped");
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
            policies = new List<IVtxoIntentSchedulingPolicy> { GetDefaultPolicy() };
            _logger.LogDebug("Using default policy for wallet {WalletId}", walletId);
        }

        // Get spendable coins for this wallet
        var spendableCoinsDict = await _arkadeSpender.GetSpendableCoins(
            [walletId], 
            true,
            cancellationToken);
        
        if (!spendableCoinsDict.TryGetValue(walletId, out var spendableCoins) || spendableCoins.Count == 0)
        {
            _logger.LogDebug("No spendable coins found for wallet {WalletId}", walletId);
            return;
        }
        
        // Get wallet
        var wallet = await _arkWalletService.GetWallet(walletId, cancellationToken);
        if (wallet == null)
        {
            _logger.LogWarning("Wallet {WalletId} not found", walletId);
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
                _logger.LogError(ex, "Error evaluating policy for wallet {WalletId}", walletId);
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
            _logger.LogInformation(
                "Creating scheduled intent for wallet {WalletId}: {Reason} with {Count} coins",
                wallet.Id, intentSpec.Reason, intentSpec.InputCoins.Length);
            
            var coins = intentSpec.InputCoins;
            var totalAmount = coins.Sum(c => c.Coin.TxOut.Value);
            // Use provided outputs or default to sending back to wallet
            ArkTxOut[] outputs;
            if (intentSpec.Outputs.Length > 0)
            {
                outputs = intentSpec.Outputs;
            }
            else
            {
                // Default: send all funds back to wallet (refreshes VTXOs, moves from recoverable state, etc.)
                var destination = await _arkadeSpender.GetDestination(wallet, await _clientTransport.GetServerInfoAsync(cancellationToken));


                outputs =
                [
                    new ArkTxOut(ArkTxOutType.Vtxo, totalAmount, destination.ScriptPubKey.GetDestination()!)
                ];
            }
            // Create the intent
            var intentId = await _intentService.CreateIntentAsync(
                wallet.Id,
                coins,
                outputs,
                validFrom: intentSpec.ValidFrom,
                validUntil: intentSpec.ValidUntil,
                cancellationToken: cancellationToken);
            
            _logger.LogInformation(
                "Created scheduled intent {IntentId} for wallet {WalletId} ({Reason}, {Count} coins, {Amount} sats)",
                intentId, wallet.Id, intentSpec.Reason, intentSpec.InputCoins.Length, totalAmount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, 
                "Failed to create scheduled intent for wallet {WalletId} ({Reason})",
                wallet.Id, intentSpec.Reason);
        }
    }

    private void OnWalletPolicyChanged(object? sender, string walletId)
    {
        _logger.LogInformation("Wallet policy changed for {WalletId}, reloading policies", walletId);
        
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
                _logger.LogError(ex, "Failed to reload and evaluate policies for wallet {WalletId}", walletId);
            }
        });
    }

    private async Task LoadPoliciesFromDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Wait for wallet service to be ready and get all wallets with policies
            var wallets = await _arkWalletService.GetWalletsWithPolicies(cancellationToken);
            
            _logger.LogInformation("Loading policies for {Count} wallets from wallet service", wallets.Length);
            
            foreach (var wallet in wallets)
            {
                await LoadWalletPolicyFromWallet(wallet, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load policies from wallet service");
        }
    }

    private async Task LoadWalletPolicyAsync(string walletId, CancellationToken cancellationToken)
    {
        try
        {
            // Load wallet from service (uses cache)
            var wallet = await _arkWalletService.GetWallet(walletId, cancellationToken);
            if (wallet == null)
            {
                _logger.LogWarning("Wallet {WalletId} not found", walletId);
                _walletPolicies.TryRemove(walletId, out _);
                return;
            }
            
            await LoadWalletPolicyFromWallet(wallet, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load policies for wallet {WalletId}", walletId);
        }
    }

    private Task LoadWalletPolicyFromWallet(ArkWallet wallet, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrEmpty(wallet.IntentSchedulingPolicy))
            {
                _walletPolicies.TryRemove(wallet.Id, out _);
                _logger.LogDebug("No policies configured for wallet {WalletId}", wallet.Id);
                return Task.CompletedTask;
            }
            
            var configs = System.Text.Json.JsonSerializer.Deserialize<List<PolicyConfiguration>>(wallet.IntentSchedulingPolicy);
            if (configs == null || configs.Count == 0)
            {
                _walletPolicies.TryRemove(wallet.Id, out _);
                return Task.CompletedTask;
            }
            
            var policies = new List<IVtxoIntentSchedulingPolicy>();
            foreach (var config in configs)
            {
                var logger = Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<FluentVtxoPolicy>(
                    Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
                var policy = FluentVtxoPolicy.FromConfiguration(config, logger);
                policies.Add(policy);
            }
            
            _walletPolicies[wallet.Id] = policies;
            _logger.LogInformation("Loaded {Count} policies for wallet {WalletId}", policies.Count, wallet.Id);
            
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load policies for wallet {WalletId}", wallet.Id);
            return Task.CompletedTask;
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
            
            await _arkWalletService.UpdateWalletIntentSchedulingPolicy(walletId, policyJson, cancellationToken);
            _logger.LogDebug("Saved policies for wallet {WalletId}", walletId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save policies for wallet {WalletId}", walletId);
        }
    }

    /// <summary>
    /// Register default policies for all wallets that don't have custom policies
    /// </summary>
    private async Task RegisterDefaultPoliciesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var walletsWithPolicies = await _arkWalletService.GetWalletsWithPolicies(cancellationToken);
            var walletsWithPoliciesSet = walletsWithPolicies.Select(w => w.Id).ToHashSet();
            
            // For wallets without custom policies, register default policy
            // The default policy will be applied during evaluation to any wallet that doesn't have custom policies
            // We'll check this dynamically in EvaluateWalletPoliciesAsync
            
            _logger.LogInformation("Default policy will be applied to wallets without custom policies");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register default policies");
        }
    }
    
    /// <summary>
    /// Get or create default policy for a wallet
    /// </summary>
    private IVtxoIntentSchedulingPolicy GetDefaultPolicy()
    {
        return new FluentVtxoPolicy(
            _policyLogger)
            .WhenExpiringWithin(TimeSpan.FromDays(1))
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
