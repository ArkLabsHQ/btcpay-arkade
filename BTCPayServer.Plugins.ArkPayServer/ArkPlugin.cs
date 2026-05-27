using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.Payouts;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Plugins.ArkPayServer.Services.WalletLogger;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Blockchain;
using NArk.Hosting;
using NArk.Core.Models.Options;
using NArk.Core.Services;
using NArk.Storage.EfCore.Entities;
using NArk.Storage.EfCore.Hosting;
using NArk.Swaps.Boltz;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Services;
using NBitcoin;
using System.Text.Json;
using BTCPayServer.Plugins.ArkPayServer.Services.Policies;
using Microsoft.EntityFrameworkCore;
using NArk.Core.Sweeper;

namespace BTCPayServer.Plugins.ArkPayServer;

public class ArkadePlugin : BaseBTCPayServerPlugin
{
    internal const string CheckoutBodyComponentName = "arkadeCheckoutBody";

    internal static readonly PaymentMethodId ArkadePaymentMethodId = new("ARKADE");
    internal static readonly PayoutMethodId ArkadePayoutMethodId = PayoutMethodId.Parse("ARKADE");

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.8" }
    ];

    public override void Execute(IServiceCollection services)
    {
        var pluginServices = (PluginServiceCollection)services;
        var networkConfig = GetNetworkConfig(pluginServices);

        if (networkConfig is null) return;

        // BTCPay plugin services
        RegisterBtcPayServices(services);

        // Database
        RegisterDatabase(services);

        // NArk storage implementations (SDK)
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
        services.AddSingleton<IDbContextFactory<ArkPluginDbContext>>(sp => sp.GetRequiredService<ArkPluginDbContextFactory>());

        services.AddStartupTask<ArkPluginMigrationRunner>();
    }

    private static void RegisterNArkStorage(IServiceCollection services)
    {
        services.AddArkEfCoreStorage<ArkPluginDbContext>(opts =>
        {
            opts.Schema = "BTCPayServer.Plugins.Ark";
            opts.ContractSearchProvider = (query, searchText) =>
            {
                var pattern = $"%{searchText}%";
                return query.Where(c =>
                    Microsoft.EntityFrameworkCore.EF.Functions.ILike(c.Script, pattern) ||
                    Microsoft.EntityFrameworkCore.EF.Functions.ILike(c.Type, pattern) ||
                    Microsoft.EntityFrameworkCore.EF.Functions.ILike(c.MetadataJson ?? "", pattern));
            };
        });
    }

    private static void RegisterNArkCore(IServiceCollection services, ArkNetworkConfig networkConfig)
    {
        // Safety service
        services.AddSingleton<ISafetyService, NArk.Safety.AsyncKeyedLock.AsyncSafetyService>();

        // Unified blockchain backend (chain time + boarding-UTXO lookup +
        // broadcast + tx status + fee estimation).
        //
        // Default backend is NBXplorerBlockchain, which reaches into
        // ExplorerClient.RPCClient.SendCommandAsync for chain time, fee
        // estimation, etc.
        //
        // When the BTCPay Electrum plugin
        // (Kukks/BTCPayServerPlugins/Plugins/BTCPayServer.Plugins.Electrum)
        // is co-installed, it rip-and-replaces NBXplorer's DI registrations
        // and substitutes its own ExplorerClient shim whose RPCClient is
        // null — NBXplorerBlockchain would NRE on every chain-time / fee
        // call. Detect that case by the registered ExplorerClientProvider's
        // concrete type name and swap in EsploraBlockchain (REST against
        // the network's default Esplora endpoint) instead.
        //
        // The inner provider's logger is passed so the cache-fallback
        // warning (emitted when the chain-time call fails transiently
        // and we serve the cached value) is visible in plugin logs
        // rather than swallowed.
        services.AddSingleton<IBitcoinBlockchain>(provider =>
        {
            var explorerClientProvider = provider.GetRequiredService<ExplorerClientProvider>();
            var providerTypeName = explorerClientProvider.GetType().FullName ?? "";

            // Two complementary signals because either alone is fragile:
            //  - Type-name "Electrum" catches the documented Electrum plugin
            //    even if it later wraps the provider in something whose
            //    RPCClient happens to be non-null.
            //  - RPCClient == null is the *actual* failure condition for
            //    NBXplorerBlockchain (it dereferences .RPCClient on every
            //    chain-time / fee / broadcast call). Any future shim that
            //    nulls it out — Electrum or otherwise — gets caught here
            //    rather than NRE'ing in production.
            var btcExplorer = explorerClientProvider.GetExplorerClient("BTC");
            var rpcClientNull = btcExplorer?.RPCClient is null;
            var typeNameLooksElectrum = providerTypeName.Contains("Electrum", StringComparison.OrdinalIgnoreCase);
            var useEsporaFallback = typeNameLooksElectrum || rpcClientNull;

            var pluginLogger = provider.GetService<ILogger<ArkadePlugin>>();

            if (useEsporaFallback)
            {
                var trigger = (typeNameLooksElectrum, rpcClientNull) switch
                {
                    (true, true) => "type-name match + RPCClient is null",
                    (true, false) => "type-name match",
                    (false, true) => "RPCClient is null on the registered ExplorerClient",
                    _ => "unknown"
                };

                var network = provider.GetRequiredService<ArkNetworkConfig>();
                var esploraUri = network.EsploraUri;
                if (string.IsNullOrWhiteSpace(esploraUri))
                {
                    throw new InvalidOperationException(
                        $"Detected an NBXplorer-incompatible ExplorerClientProvider ({providerTypeName}, trigger: {trigger}) " +
                        "but ArkNetworkConfig.EsploraUri is null for this network. " +
                        "NBXplorerBlockchain reaches into ExplorerClient.RPCClient which the shim does not expose; " +
                        "the Arkade plugin needs an Esplora endpoint as a fallback. " +
                        "Set 'esplora' in ark.json or pick a network preset (Mainnet/Mutinynet/Signet/Regtest) that includes it.");
                }

                var esploraLogger = provider.GetService<ILogger<EsploraBlockchain>>();
                pluginLogger?.LogInformation(
                    "NBXplorer-incompatible ExplorerClientProvider detected ({Provider}, trigger: {Trigger}); using EsploraBlockchain at {Uri} for chain time / UTXO lookup / broadcast / fee estimation",
                    providerTypeName, trigger, esploraUri);
                return new EsploraBlockchain(new Uri(esploraUri), esploraLogger);
            }

            var nbxLogger = provider.GetService<ILogger<NBXplorerBlockchain>>();
            pluginLogger?.LogInformation(
                "Using NBXplorerBlockchain for chain time / UTXO lookup / broadcast / fee estimation (ExplorerClientProvider: {Provider}, RPCClient: non-null)",
                providerTypeName);
            return new NBXplorerBlockchain(btcExplorer, nbxLogger);
        });

        // Intent scheduler
        services.Configure<SimpleIntentSchedulerOptions>(options =>
            options.Threshold = TimeSpan.FromDays(1));
        services.AddSingleton<IIntentScheduler, SimpleIntentScheduler>();

        // Intent-generation cadence override. NArk's IntentGenerationService
        // falls back to a 5-minute poll when PollInterval is unset; that
        // default governs how quickly imported notes and near-expiry VTXOs
        // turn into batch intents. Left unset here so production behaviour
        // is unchanged — operators (and the e2e suite) can shorten it via
        // BTCPAY_ARKINTENTPOLLSECONDS without a code change.
        services.AddOptions<IntentGenerationServiceOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                var seconds = configuration.GetValue<int?>("ARKINTENTPOLLSECONDS");
                if (seconds is > 0)
                    options.PollInterval = TimeSpan.FromSeconds(seconds.Value);
            });

        // Wallet provider
        services.AddSingleton<NArk.Abstractions.Wallets.IWalletProvider, NArk.Core.Wallet.DefaultWalletProvider>();

        // BoardingUtxoSyncService consumes IBitcoinBlockchain.GetUtxosAsync — the
        // NBXplorer-backed registration above implements it for boarding lookup.
        services.AddSingleton<BoardingUtxoSyncService>();

        // Core services and network config (includes caching transport by default)
        services.AddArkCoreServices();
        services.AddArkNetwork(networkConfig);
    }

    private static void RegisterPluginServices(IServiceCollection services)
    {
        // Tracks the background wallet-recovery job per wallet (import-triggered + manual Rescan).
        services.AddSingleton<RecoveryStatusTracker>();

        // Per-wallet diagnostic log store. Captures NArk + plugin log
        // entries that carry a `WalletId` (either via BeginScope or the
        // structured-log args) into a rolling file per wallet so the
        // merchant can download a wallet-scoped log when asking for
        // support. See Services/WalletLogger/.
        // This factory must NOT resolve an ILogger<>. The companion
        // ILoggerProvider registration below means IWalletLogStore is built
        // while the host LoggerFactory is itself being constructed — pulling
        // an ILogger<T> through DI here re-enters that half-built factory
        // and hangs plugin startup.
        services.AddSingleton<IWalletLogStore>(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var dataDir = new DataDirectories().Configure(configuration).DataDir;
            var logDir = Path.Combine(dataDir, "Plugins", "ArkPayServer", "wallet-logs");
            return new RollingFileWalletLogStore(logDir);
        });
        services.AddSingleton<ILoggerProvider>(sp =>
            new WalletScopedLoggerProvider(sp.GetRequiredService<IWalletLogStore>()));

        services.AddSingleton<ArkadeSpendingService>();

        // Remote-signer transport seam.
        //
        // NArk's DefaultWalletProvider takes IRemoteSignerTransport? as an
        // optional ctor param. ASP.NET Core DI invokes the registered factory
        // when the consumer is constructed regardless of the C# default-value
        // sugar, so the factory MUST NOT throw — it would abort the whole
        // host build the moment the plugin loaded, even on stores that have
        // no Remote wallets to sign for. Instead:
        //
        //  - When the companion BTCPayServer.Plugins.App plugin is installed
        //    it registers an IBTCPayAppDeviceProxy that bridges signing calls
        //    to a connected BTCPayApp device over its SignalR hub; we forward
        //    that as the IRemoteSignerTransport.
        //  - When no companion plugin is installed, we hand back a
        //    MissingDeviceProxyTransport sentinel whose KnowsWalletAsync returns
        //    false for every wallet — so a wallet imported via the watch-only
        //    flow falls through to genuine watch-only (DefaultWalletProvider
        //    returns null from GetSignerAsync). The three signing methods still
        //    throw a descriptive "install the App companion plugin" message as
        //    defence-in-depth, but the happy path now matches user intent: pick
        //    "watch-only" and you get watch-only, not a runtime nag.
        services.AddSingleton<IRemoteSignerTransport>(sp =>
            sp.GetService<IBTCPayAppDeviceProxy>()
            ?? (IRemoteSignerTransport)new MissingDeviceProxyTransport());

        // Tracks Arkade-operator reachability so plugin pages can show a friendly
        // "operator unavailable" banner instead of leaking raw gRPC/HTTP errors.
        services.AddSingleton<ArkOperatorHealthService>();

        services.AddSingleton<ISweepPolicy, DestinationSweepPolicy>();

        services.AddSingleton<ArkadeCheckoutModelExtension>();
        services.AddSingleton<ICheckoutModelExtension>(sp => sp.GetRequiredService<ArkadeCheckoutModelExtension>());
        services.AddSingleton<IGlobalCheckoutModelExtension>(sp => sp.GetRequiredService<ArkadeCheckoutModelExtension>());

        services.AddSingleton<ArkadeCheckoutCheatModeExtension>();
        services.AddSingleton<ICheckoutCheatModeExtension>(sp => sp.GetRequiredService<ArkadeCheckoutCheatModeExtension>());

        services.AddSingleton<ArkContractInvoiceListener>();
        services.AddHostedService(sp => sp.GetRequiredService<ArkContractInvoiceListener>());

        services.AddSingleton<BoardingTransactionListener>();
        services.AddHostedService(sp => sp.GetRequiredService<BoardingTransactionListener>());

        services.AddSingleton<BoardingUtxoPollService>();
        services.AddHostedService(sp => sp.GetRequiredService<BoardingUtxoPollService>());
    }

    private static void RegisterUIExtensions(IServiceCollection services)
    {
        services.AddUIExtension("checkout-end", "Arkade/ArkadeMethodCheckout");
        services.AddUIExtension("dashboard-setup-guide-payment", "/Views/Ark/DashboardSetupGuidePayment.cshtml");
        services.AddUIExtension("store-invoices-payments", "/Views/Ark/ArkPaymentData.cshtml");
        services.AddUIExtension("store-wallets-nav", "/Views/Ark/ArkWalletNav.cshtml");
        services.AddUIExtension("ln-payment-method-setup-tab", "/Views/Lightning/LNPaymentMethodSetupTab.cshtml");
        services.AddUIExtension("dashboard", "/Views/Ark/ArkDashboardWidget.cshtml");
        services.AddUIExtension("dashboard", "/Views/Ark/ArkActivityDashboardWidget.cshtml");
    }

    private static void RegisterBoltzServices(IServiceCollection services, ArkNetworkConfig networkConfig)
    {
        if (!string.IsNullOrWhiteSpace(networkConfig.BoltzUri))
        {
            services.AddHttpClient<BoltzClient>();
            services.AddHttpClient<CachedBoltzClient>();
            services.AddArkSwapServices();

            // Tag every Boltz swap-creation request with the BTCPay-Arkade
            // referral so Boltz can credit the integration. Mirrors the
            // wallet-side `arkade-money` referral added in arkade-os/wallet#606.
            services.Configure<NArk.Swaps.Boltz.Models.BoltzClientOptions>(o => o.ReferralId = "btcpay-arkade");

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
        // if (preset is null) return null;

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
            BoltzUri: !string.IsNullOrEmpty(fileConfig?.BoltzUri) ? fileConfig.BoltzUri : preset.BoltzUri,
            ExplorerUri: !string.IsNullOrEmpty(fileConfig?.ExplorerUri) ? fileConfig.ExplorerUri : preset.ExplorerUri,
            // EsploraUri / ElectrumWsUri / ElectrumTcpUri arrived in
            // ArkNetworkConfig via NNark dotnet-sdk#96. They MUST be carried
            // through here too — otherwise the merge silently nulls the
            // preset's Esplora endpoint and operators with a custom ark.json
            // hit the InvalidOperationException in the IBitcoinBlockchain
            // factory the moment they co-install the Electrum plugin (same
            // failure mode as v2.1.14's ExplorerUri-merge regression).
            EsploraUri: !string.IsNullOrEmpty(fileConfig?.EsploraUri) ? fileConfig.EsploraUri : preset.EsploraUri,
            ElectrumWsUri: !string.IsNullOrEmpty(fileConfig?.ElectrumWsUri) ? fileConfig.ElectrumWsUri : preset.ElectrumWsUri,
            ElectrumTcpUri: !string.IsNullOrEmpty(fileConfig?.ElectrumTcpUri) ? fileConfig.ElectrumTcpUri : preset.ElectrumTcpUri
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
                BoltzUri: null,
                ExplorerUri: "https://explorer.signet.arkade.sh",
                // Signet endpoints mirror the canonical ts-sdk defaults
                // (https://github.com/arkade-os/ts-sdk/blob/main/src/providers/onchain.ts
                // and electrum.ts). NNark only ships Mainnet/Mutinynet/Regtest
                // presets so the plugin fills these per-network here.
                EsploraUri: "https://mempool.signet.arkade.sh/api",
                ElectrumWsUri: "wss://electrum.signet.arkade.sh",
                ElectrumTcpUri: "tcp://electrum.signet.arkade.sh:50001");

        return null;
    }

    #endregion
}
