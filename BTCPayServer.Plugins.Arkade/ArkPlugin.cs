using AsyncKeyedLock;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.Payouts;
using BTCPayServer.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Blockchain.NBXplorer;
using NArk.Hosting;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Services;
using NBitcoin;
using System.Reflection;
using System.Text.Json;
using BTCPayServer.Plugins.Arkade.Data;
using BTCPayServer.Plugins.Arkade.Lightning;
using BTCPayServer.Plugins.Arkade.PaymentHandler;
using BTCPayServer.Plugins.Arkade.Payouts.Ark;
using BTCPayServer.Plugins.Arkade.Services;
using BTCPayServer.Plugins.Arkade.Services.Policies;
using BTCPayServer.Plugins.Arkade.Storage;
using NArk.Abstractions.Scripts;
using NArk.Core.Sweeper;

namespace BTCPayServer.Plugins.Arkade;

public class ArkadePlugin : BaseBTCPayServerPlugin
{
    internal const string CheckoutBodyComponentName = "arkadeCheckoutBody";

    internal static readonly PaymentMethodId ArkadePaymentMethodId = new("ARKADE");
    internal static readonly PayoutMethodId ArkadePayoutMethodId = CreatePayoutMethodId();

    private static PayoutMethodId CreatePayoutMethodId()
    {
        var constructor = typeof(PayoutMethodId).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, [typeof(string)])!;
        return (PayoutMethodId)constructor.Invoke(["ARKADE"])!;
    }

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.3" }
    ];

    private const string OldPluginIdentifier = "BTCPayServer.Plugins.ArkPayServer";

    public override void Execute(IServiceCollection services)
    {
        var pluginServices = (PluginServiceCollection)services;

        // Detect if the old plugin (ArkPayServer) is also loaded.
        // If so, schedule its removal and restart BTCPay — don't register any services
        // to avoid double-registration conflicts with the old plugin during this run.
        if (IsOldPluginLoaded())
        {
            var configuration = pluginServices.BootstrapServices.GetRequiredService<IConfiguration>();
            var pluginDir = new DataDirectories().Configure(configuration).PluginDir;

            PluginManager.QueueCommands(pluginDir, ("delete", OldPluginIdentifier));

            services.AddHostedService<DelayedRestartService>();
            return;
        }

        var networkConfig = GetNetworkConfig(pluginServices);

        if (networkConfig is null) return;

        // BTCPay plugin services
        RegisterBtcPayServices(services);

        // Database
        RegisterDatabase(services);

        // NArk storage implementations
        RegisterNArkStorage(services);

        // NArk core services
        RegisterNArkCore(services, networkConfig);

        // Plugin-specific services
        RegisterPluginServices(services);

        // UI extensions
        RegisterUIExtensions(services);

        // Boltz swap services (optional)
        RegisterBoltzServices(services, networkConfig);
    }

    private static bool IsOldPluginLoaded()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => a.GetName().Name == OldPluginIdentifier);
    }

    /// <summary>
    /// Hosted service that waits briefly for BTCPay to finish starting, then triggers a restart
    /// so the queued "delete old plugin" command executes on next boot.
    /// </summary>
    private sealed class DelayedRestartService(IHostApplicationLifetime lifetime) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = Task.Run(lifetime.StopApplication);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    #region Service Registration

    private static void RegisterBtcPayServices(IServiceCollection services)
    {
        services.AddSingleton<ILightningConnectionStringHandler, ArkLightningConnectionStringHandler>();
        services.AddSingleton<ArkadeLightningLimitsService>();

        services.AddSingleton<ArkadePaymentMethodHandler>();
        services.AddSingleton<IPaymentMethodHandler>(sp => sp.GetRequiredService<ArkadePaymentMethodHandler>());

        services.AddSingleton<ArkadePaymentLinkExtension>();
        services.AddSingleton<IPaymentLinkExtension>(sp => sp.GetRequiredService<ArkadePaymentLinkExtension>());

        services.AddSingleton<ArkPayoutHandler>();
        services.AddSingleton<IPayoutHandler>(sp => sp.GetRequiredService<ArkPayoutHandler>());

        services.AddSingleton<ArkAutomatedPayoutSenderFactory>();
        services.AddSingleton<IPayoutProcessorFactory>(sp => sp.GetRequiredService<ArkAutomatedPayoutSenderFactory>());

        services.AddDefaultPrettyName(ArkadePaymentMethodId, "Arkade");
    }

    private static void RegisterDatabase(IServiceCollection services)
    {
        services.AddSingleton<ArkPluginDbContextFactory>();

        services.AddDbContext<ArkPluginDbContext>((provider, o) =>
        {
            var factory = provider.GetRequiredService<ArkPluginDbContextFactory>();
            factory.ConfigureBuilder(o);
        });

        services.AddDbContextFactory<ArkPluginDbContext>((provider, o) =>
        {
            var factory = provider.GetRequiredService<ArkPluginDbContextFactory>();
            factory.ConfigureBuilder(o);
        });

        services.AddStartupTask<ArkPluginMigrationRunner>();
    }

    private static void RegisterNArkStorage(IServiceCollection services)
    {
        services.AddSingleton<EfCoreVtxoStorage>();
        services.AddSingleton<IVtxoStorage>(sp => sp.GetRequiredService<EfCoreVtxoStorage>());
        services.AddSingleton<IActiveScriptsProvider>(sp => sp.GetRequiredService<EfCoreVtxoStorage>());

        services.AddSingleton<EfCoreContractStorage>();
        services.AddSingleton<IContractStorage>(sp => sp.GetRequiredService<EfCoreContractStorage>());
        services.AddSingleton<IActiveScriptsProvider>(sp => sp.GetRequiredService<EfCoreContractStorage>());

        services.AddSingleton<EfCoreIntentStorage>();
        services.AddSingleton<IIntentStorage>(sp => sp.GetRequiredService<EfCoreIntentStorage>());

        services.AddSingleton<EfCoreSwapStorage>();
        services.AddSingleton<ISwapStorage>(sp => sp.GetRequiredService<EfCoreSwapStorage>());

        services.AddSingleton<EfCoreWalletStorage>();
    }

    private static void RegisterNArkCore(IServiceCollection services, ArkNetworkConfig networkConfig)
    {
        // Safety service
        services.AddSingleton<ISafetyService, NArk.Safety.AsyncKeyedLock.AsyncSafetyService>();

        // Wallet provider
        services.AddSingleton<IWalletProvider, Wallet.PluginWalletAdapter>();

        // Chain time provider (needs NBXplorer)
        services.AddSingleton<ChainTimeProvider>(provider =>
        {
            var explorerClientProvider = provider.GetRequiredService<ExplorerClientProvider>();
            return new ChainTimeProvider(explorerClientProvider.GetExplorerClient("BTC"));
        });
        services.AddSingleton<IChainTimeProvider>(sp => sp.GetRequiredService<ChainTimeProvider>());

        // Intent scheduler
        services.Configure<SimpleIntentSchedulerOptions>(options =>
            options.Threshold = TimeSpan.FromDays(1));
        services.AddSingleton<IIntentScheduler, SimpleIntentScheduler>();

        // Core services and network config (includes caching transport by default)
        services.AddArkCoreServices();
        services.AddArkNetwork(networkConfig);
    }

    private static void RegisterPluginServices(IServiceCollection services)
    {
        services.AddSingleton<ArkadeSpendingService>();

        services.AddSingleton<ISweepPolicy, DestinationSweepPolicy>();

        services.AddSingleton<ArkadeCheckoutModelExtension>();
        services.AddSingleton<ICheckoutModelExtension>(sp => sp.GetRequiredService<ArkadeCheckoutModelExtension>());

        services.AddSingleton<ArkadeCheckoutCheatModeExtension>();
        services.AddSingleton<ICheckoutCheatModeExtension>(sp => sp.GetRequiredService<ArkadeCheckoutCheatModeExtension>());

        services.AddSingleton<ArkContractInvoiceListener>();
        services.AddHostedService(sp => sp.GetRequiredService<ArkContractInvoiceListener>());
    }

    private static void RegisterUIExtensions(IServiceCollection services)
    {
        services.AddUIExtension("checkout-end", "Arkade/ArkadeMethodCheckout");
        services.AddUIExtension("dashboard-setup-guide-payment", "/Views/Ark/DashboardSetupGuidePayment.cshtml");
        services.AddUIExtension("store-invoices-payments", "/Views/Ark/ArkPaymentData.cshtml");
        services.AddUIExtension("store-wallets-nav", "/Views/Ark/ArkWalletNav.cshtml");
        services.AddUIExtension("ln-payment-method-setup-tab", "/Views/Lightning/LNPaymentMethodSetupTab.cshtml");
        services.AddUIExtension("dashboard", "/Views/Ark/ArkDashboardWidget.cshtml");
    }

    private static void RegisterBoltzServices(IServiceCollection services, ArkNetworkConfig networkConfig)
    {
        if (!string.IsNullOrWhiteSpace(networkConfig.BoltzUri))
        {
            services.AddHttpClient<BoltzClient>();
            services.AddHttpClient<CachedBoltzClient>();
            services.AddArkSwapServices();

            services.AddUIExtension("ln-payment-method-setup-tabhead", "/Views/Ark/ArkLNSetupTabhead.cshtml");

            services.AddSingleton<ArkadeLNURLPayRequestFilter>();
            services.AddSingleton<IPluginHookFilter>(sp => sp.GetRequiredService<ArkadeLNURLPayRequestFilter>());
        }
        else
        {
            // Null implementations for optional dependencies
            services.AddSingleton<BoltzClient>(_ => null!);
            services.AddSingleton<CachedBoltzClient>(_ => null!);
            services.AddSingleton<SwapsManagementService>(_ => null!);
            services.AddSingleton<BoltzLimitsValidator>(_ => null!);
        }
    }

    #endregion

    #region Network Configuration

    private static ArkNetworkConfig? GetNetworkConfig(PluginServiceCollection pluginServices)
    {
        var configuration = pluginServices.BootstrapServices.GetRequiredService<IConfiguration>();
        var networkType = DefaultConfiguration.GetNetworkType(configuration);

        // Start with preset for the network
        var preset = GetNetworkPreset(networkType);
        if (preset is null) return null;

        // Check for config file override
        var dataDir = new DataDirectories().Configure(configuration).DataDir;
        var configPath = Path.Combine(dataDir, "ark.json");

        if (!File.Exists(configPath))
            return preset;

        // Merge file config with preset (file values override preset)
        var json = File.ReadAllText(configPath);
        var fileConfig = JsonSerializer.Deserialize<ArkNetworkConfig>(json);

        return new ArkNetworkConfig(
            ArkUri: !string.IsNullOrEmpty(fileConfig?.ArkUri) ? fileConfig.ArkUri : preset.ArkUri,
            ArkadeWalletUri: !string.IsNullOrEmpty(fileConfig?.ArkadeWalletUri) ? fileConfig.ArkadeWalletUri : preset.ArkadeWalletUri,
            BoltzUri: !string.IsNullOrEmpty(fileConfig?.BoltzUri) ? fileConfig.BoltzUri : preset.BoltzUri
        );
    }

    private static ArkNetworkConfig? GetNetworkPreset(ChainName networkType)
    {
        if (networkType == NBitcoin.Bitcoin.Instance.Mainnet.ChainName)
            return ArkNetworkConfig.Mainnet;
        if (networkType == NBitcoin.Bitcoin.Instance.Mutinynet.ChainName)
            return ArkNetworkConfig.Mutinynet;
        if (networkType == ChainName.Regtest)
            return ArkNetworkConfig.Regtest;
        if (networkType == NBitcoin.Bitcoin.Instance.Signet.ChainName)
            return new ArkNetworkConfig(
                ArkUri: "https://signet.arkade.sh",
                ArkadeWalletUri: "https://signet.arkade.money",
                BoltzUri: null);

        return null;
    }

    #endregion
}
