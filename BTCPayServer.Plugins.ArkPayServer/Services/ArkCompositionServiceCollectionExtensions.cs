using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NArk.ArkadeIntents;
using NArk.Storage.EfCore.Storage;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public static class ArkCompositionServiceCollectionExtensions
{
    public static IServiceCollection AddArkCompositionSdk(this IServiceCollection services)
    {
        services.AddHttpClient("ArkCompositionEvm").RemoveAllLoggers();
        services.AddHttpClient("ArkCompositionRfq");
        services.Replace(ServiceDescriptor.Singleton<IArkadeIntentStorage>(provider => new ArkProtectedIntentStorage(
            provider.GetRequiredService<EfCoreArkadeIntentStorage>(), provider.GetRequiredService<IDataProtectionProvider>())));
        services.TryAddSingleton<ArkCompositionEvmContextFactory>();
        services.TryAddSingleton<ArkCompositionSolverFactory>();
        return services;
    }
}
