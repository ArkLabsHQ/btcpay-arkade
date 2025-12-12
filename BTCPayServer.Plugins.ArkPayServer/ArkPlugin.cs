using Ark.V1;
using AsyncKeyedLock;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payouts;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NArk;
using NArk.Boltz.Client;
using NArk.Services;
using NBitcoin;
using System.Reflection;
using System.Text.Json;
using BTCPayServer.PayoutProcessors;
using Grpc.Net.ClientFactory;
using BTCPayServer.Plugins.ArkPayServer.Cache;
using BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;
using NArk.Services.Abstractions;

namespace BTCPayServer.Plugins.ArkPayServer;

public class ArkadePlugin : BaseBTCPayServerPlugin
{
    internal const string PluginNavKey = nameof(ArkadePlugin) + "Nav";
    internal const string ArkadeDisplayName = "Arkade";
    internal const string CheckoutBodyComponentName = "arkadeCheckoutBody";

    internal static readonly PaymentMethodId ArkadePaymentMethodId = new PaymentMethodId("ARKADE");
    
    internal static readonly PayoutMethodId ArkadePayoutMethodId = Create();

    private static PayoutMethodId Create()
    {
        //use reflection to access ctor of PayoutMethodId and create it
        var constructor = typeof(PayoutMethodId).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string) })!;
        return (PayoutMethodId) constructor.Invoke(["ARKADE"])!;
    }
    
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new () { Identifier = nameof(BTCPayServer), Condition = ">=2.1.0" }
    ];

    public override void Execute(IServiceCollection serviceCollection)
    {
        var pluginServiceCollection = (PluginServiceCollection) serviceCollection;
        
        var (arkUri, boltzUri, arkadeWalletUri) = GetServiceUris(pluginServiceCollection);
        
        if (arkUri is null) return;

        var config = new ArkConfiguration(arkUri,  arkadeWalletUri, boltzUri);
        
        SetupBtcPayPluginServices(serviceCollection);
        
        serviceCollection.AddSingleton(config);
        serviceCollection.AddSingleton<ArkadePaymentMethodHandler>();
        serviceCollection.AddSingleton<ArkPluginDbContextFactory>();
        serviceCollection.AddSingleton<AsyncKeyedLocker>();
        
        serviceCollection.AddSingleton<ArkadeWalletSignerProvider>();
        serviceCollection.AddSingleton<ArkadeContractSweeper>();
        serviceCollection.AddDbContext<ArkPluginDbContext>((provider, o) =>
        {
            var factory = provider.GetRequiredService<ArkPluginDbContextFactory>();
            factory.ConfigureBuilder(o);
        });
        serviceCollection.AddStartupTask<ArkPluginMigrationRunner>();

        serviceCollection.AddSingleton<ArkWalletService>();
        serviceCollection.AddSingleton<ArkadeSpender>();
        serviceCollection.AddSingleton<ArkTransactionBuilder>();
        serviceCollection.AddSingleton<ArkadeCheckoutModelExtension>();
        serviceCollection.AddSingleton<ArkadeCheckoutCheatModeExtension>();
        serviceCollection.AddSingleton<ICheckoutModelExtension>(provider => provider.GetRequiredService<ArkadeCheckoutModelExtension>());
        serviceCollection.AddSingleton<ICheckoutCheatModeExtension>(provider => provider.GetRequiredService<ArkadeCheckoutCheatModeExtension>());
        serviceCollection.AddSingleton<IArkadeMultiWalletSigner>(provider => provider.GetRequiredService<ArkWalletService>());
        serviceCollection.AddSingleton<ArkVtxoSynchronizationService>();
        serviceCollection.AddSingleton<ArkContractInvoiceListener>();
        serviceCollection.AddSingleton<ArkIntentService>();
        serviceCollection.AddSingleton<ArkIntentScheduler>();
        serviceCollection.AddSingleton<BitcoinTimeChainProvider>();
        serviceCollection.AddHostedService<ArkWalletService>(provider => provider.GetRequiredService<ArkWalletService>());
        serviceCollection.AddHostedService<ArkVtxoSynchronizationService>(provider => provider.GetRequiredService<ArkVtxoSynchronizationService>());
        serviceCollection.AddHostedService<ArkContractInvoiceListener>(provider => provider.GetRequiredService<ArkContractInvoiceListener>());
        serviceCollection.AddHostedService<ArkadeContractSweeper>(provider => provider.GetRequiredService<ArkadeContractSweeper>());
        serviceCollection.AddHostedService<ArkIntentService>(provider => provider.GetRequiredService<ArkIntentService>());
        // serviceCollection.AddHostedService<ArkIntentScheduler>(provider => provider.GetRequiredService<ArkIntentScheduler>());
        serviceCollection.AddHostedService<BitcoinTimeChainProvider>(provider => provider.GetRequiredService<BitcoinTimeChainProvider>());
        
        serviceCollection.AddSingleton<ArkadeSpendingService>();
        
        serviceCollection.AddSingleton<TrackedContractsCache>();
        serviceCollection.AddHostedService<TrackedContractsCache>(provider => provider.GetRequiredService<TrackedContractsCache>());

        // Register Arkade checkout view
        serviceCollection.AddUIExtension("checkout-end", "Arkade/ArkadeMethodCheckout");
        serviceCollection.AddUIExtension("dashboard-setup-guide-payment", "/Views/OldArk/DashboardSetupGuidePayment.cshtml");
        serviceCollection.AddUIExtension("store-invoices-payments", "/Views/OldArk/ArkPaymentData.cshtml");
        // Display Ark as a wallet type in navigation sidebar
        serviceCollection.AddUIExtension("store-wallets-nav", "/Views/Ark/ArkWalletNav.cshtml");
        
        // Display ARK instructions in the Lightning setup screen
        serviceCollection.AddUIExtension(
            location: "ln-payment-method-setup-tab",
            partialViewName: "/Views/Lightning/LNPaymentMethodSetupTab.cshtml");
        
       
        serviceCollection.AddGrpcClient<ArkService.ArkServiceClient>(options =>
        {
            options.Address = new Uri(config.ArkUri);
            options.InterceptorRegistrations.Add(new InterceptorRegistration(InterceptorScope.Client, provider => new DeadlineInterceptor(TimeSpan.FromSeconds(10))));
            
        });
        
        serviceCollection.AddGrpcClient<IndexerService.IndexerServiceClient>(options =>
        {
            options.Address = new Uri(config.ArkUri);
            options.InterceptorRegistrations.Add(new InterceptorRegistration(InterceptorScope.Client, provider => new DeadlineInterceptor(TimeSpan.FromSeconds(10))));

        });

        // Register Ark services
        serviceCollection.AddSingleton<CachedOperatorTermsService>();
        serviceCollection.AddSingleton<IOperatorTermsService, CachedOperatorTermsService>(provider => provider.GetRequiredService<CachedOperatorTermsService>());

        // Register Boltz services only if BoltzUri is configured
        if (!string.IsNullOrWhiteSpace(config.BoltzUri))
        {
            serviceCollection.AddHttpClient<BoltzClient>(client =>
            {
                client.BaseAddress = new Uri(config.BoltzUri);
            });
            
            // Register the Boltz swap services only when BoltzClient is available
            serviceCollection.AddSingleton<BoltzSwapService>();
            serviceCollection.AddSingleton<BoltzService>();
            serviceCollection.AddHostedService<BoltzService>(provider => provider.GetRequiredService<BoltzService>());
            serviceCollection.AddUIExtension("ln-payment-method-setup-tabhead", "/Views/OldArk/ArkLNSetupTabhead.cshtml");
            
            // Register LNURL filter to apply Boltz limits
            serviceCollection.AddSingleton<ArkadeLNURLPayRequestFilter>();
            serviceCollection.AddSingleton<IPluginHookFilter>(provider => provider.GetRequiredService<ArkadeLNURLPayRequestFilter>());
        }
        else
        {
            // Register null implementations so DI can inject null for optional dependencies
            serviceCollection.AddSingleton<BoltzClient>(provider => null!);
            serviceCollection.AddSingleton<BoltzService>(provider => null!);
        }
    }

    
    private static (string? ArkUri, string? BoltzUri, string? ArkadeWalletUri) GetServiceUris(PluginServiceCollection pluginServiceCollection)
    {
        var networkType = 
            DefaultConfiguration.GetNetworkType(
                pluginServiceCollection
                    .BootstrapServices
                    .GetRequiredService<IConfiguration>()
            );
        
        var arkUri = GetArkServiceUri(networkType);
        var boltzUri = GetBoltzServiceUri(networkType);
        var arkadeWalletUri = GetArkadeWalletServiceUri(networkType);
        
        var configurationServices =
            pluginServiceCollection
                .BootstrapServices
                .GetRequiredService<IConfiguration>();
        
        var arkadeFilePath =
            Path.Combine(new DataDirectories().Configure(configurationServices).DataDir, "ark.json");
        
        if (File.Exists(arkadeFilePath))
        {
            var json = File.ReadAllText(arkadeFilePath);
            var config = JsonSerializer.Deserialize<ArkConfiguration>(json);

            if(!string.IsNullOrEmpty(config?.BoltzUri))
            {
                boltzUri = config.BoltzUri;
            }
            
            if(!string.IsNullOrEmpty(config?.ArkUri))
            {
                arkUri = config.ArkUri;
            }  
            if(!string.IsNullOrEmpty(config?.ArkadeWalletUri))
            {
                arkadeWalletUri = config.ArkadeWalletUri;
            }
            
            
            
        }


        return (arkUri, boltzUri, arkadeWalletUri);
    }
    
    private static string? GetArkadeWalletServiceUri(ChainName networkType)
    {
        if (networkType == NBitcoin.Bitcoin.Instance.Mainnet.ChainName)
            return "https://arkade.money";
        if (networkType == NBitcoin.Bitcoin.Instance.Mutinynet.ChainName)
            return "https://mutinynet.arkade.money";
        if (networkType == NBitcoin.Bitcoin.Instance.Signet.ChainName)
            return "https://signet.arkade.money";
        if (networkType == ChainName.Regtest)
            return "http://localhost:3002";
        return null;
    }

    private static string? GetArkServiceUri(ChainName networkType)
    {
        if (networkType == NBitcoin.Bitcoin.Instance.Mainnet.ChainName)
            return "https://arkade.computer";
        if (networkType == NBitcoin.Bitcoin.Instance.Mutinynet.ChainName)
            return "https://mutinynet.arkade.sh";
        if (networkType == NBitcoin.Bitcoin.Instance.Signet.ChainName)
            return "https://signet.arkade.sh";
        if (networkType == ChainName.Regtest)
            return "http://localhost:7070";
        return null;
    }
    
    private static string? GetBoltzServiceUri(ChainName networkType)
    {
        if (networkType == NBitcoin.Bitcoin.Instance.Mainnet.ChainName)
        
            return "https://api.ark.boltz.exchange/";
        
        if (networkType == NBitcoin.Bitcoin.Instance.Mutinynet.ChainName)
            return "https://api.boltz.mutinynet.arkade.sh/";
        if (networkType == ChainName.Regtest)
            return "http://localhost:9001/";
        return null;
    }

    private static void SetupBtcPayPluginServices(IServiceCollection serviceCollection)
    {
        // Register ArkConnectionStringHandler so LightningClientFactoryService can create the client
        serviceCollection.AddSingleton<ILightningConnectionStringHandler, ArkLightningConnectionStringHandler>();
        serviceCollection.AddSingleton<ArkadeLightningLimitsService>();
        serviceCollection.AddSingleton<ArkadePaymentLinkExtension>();
        serviceCollection.AddSingleton<IPaymentLinkExtension>(provider => provider.GetRequiredService<ArkadePaymentLinkExtension>());
        serviceCollection.AddSingleton<IPaymentMethodHandler>(provider => provider.GetRequiredService<ArkadePaymentMethodHandler>());
        serviceCollection.AddSingleton<ArkPayoutHandler>();
        serviceCollection.AddSingleton<IPayoutHandler>(provider => provider.GetRequiredService<ArkPayoutHandler>());
        serviceCollection.AddSingleton<ArkAutomatedPayoutSenderFactory>();
        serviceCollection.AddSingleton<IPayoutProcessorFactory>(provider => provider.GetRequiredService<ArkAutomatedPayoutSenderFactory>());
        
        serviceCollection.AddDefaultPrettyName(ArkadePaymentMethodId, "Arkade");
    }
}