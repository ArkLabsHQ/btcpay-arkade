using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using BTCPayServer.Plugins.ArkPayServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public sealed record ArkCompositionRouteKey(string StoreId, Guid RouteId);

public interface IArkCompositionExecutionJournal
{
    Task<IReadOnlyList<ArkCompositionRouteKey>> ListAsync(int skip, int take, CancellationToken cancellationToken);
    Task<ArkCompositionRoute?> GetAsync(string storeId, Guid routeId, CancellationToken cancellationToken);
}

public interface IArkCompositionExecutionLock
{
    bool SupportsCrossProcessExecution => false;
    ValueTask<IAsyncDisposable> AcquireAsync(string scope, CancellationToken cancellationToken);
}

public interface IArkCompositionPaymentSink
{
    Task SettleAsync(ArkCompositionRoute route, NArk.ArkadeIntents.Composition.ComposedSwapExecutionResult result,
        CancellationToken cancellationToken);
}

public sealed class ArkCompositionExecutionJournal(ArkCompositionRouteRepository repository)
    : IArkCompositionExecutionJournal
{
    public async Task<IReadOnlyList<ArkCompositionRouteKey>> ListAsync(int skip, int take,
        CancellationToken cancellationToken)
    {
        // The journal is the route index; swap state is read live from SDK intent storage
        // by the execution service. Only rows with a customer prompt need advancing.
        var page = await repository.ListAllWithPrompts(skip, take, cancellationToken);
        return page.Select(r => new ArkCompositionRouteKey(r.StoreId, r.RouteId)).ToArray();
    }

    public Task<ArkCompositionRoute?> GetAsync(string storeId, Guid routeId, CancellationToken cancellationToken) =>
        repository.Get(storeId, routeId, cancellationToken);
}

public sealed class ArkCompositionPostgresExecutionLock(IDbContextFactory<ArkPluginDbContext> factory)
    : IArkCompositionExecutionLock
{
    public bool SupportsCrossProcessExecution
    {
        get
        {
            try
            {
                using var context = factory.CreateDbContext();
                return context.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";
            }
            catch { return false; }
        }
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(string scope, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var context = await factory.CreateDbContextAsync(cancellationToken);
        try
        {
            if (context.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL")
                throw new InvalidOperationException("Composition execution requires a cross-process PostgreSQL lock.");
            await context.Database.BeginTransactionAsync(cancellationToken);
            var key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes("ArkComposition:" + scope)));
            await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
            return context;
        }
        catch { await context.DisposeAsync(); throw; }
    }
}

public sealed class ArkCompositionExecutionHostedService(IArkCompositionExecutionJournal journal,
    ArkComposedSwapExecutionService execution, ILogger<ArkCompositionExecutionHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                for (var skip = 0; ; skip += 100)
                {
                    var routes = await journal.ListAsync(skip, 100, stoppingToken);
                    foreach (var route in routes)
                    {
                        try { await execution.AdvanceAsync(route.StoreId, route.RouteId, stoppingToken); }
                        catch (Exception) when (!stoppingToken.IsCancellationRequested)
                        {
                            logger.LogWarning("Composition route {RouteId} awaits an execution or settlement retry.", route.RouteId);
                        }
                    }
                    if (routes.Count < 100) break;
                }
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Composition execution could not read the route index.");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}

public static class ArkCompositionExecutionRegistration
{
    public static IServiceCollection AddArkCompositionExecution(this IServiceCollection services)
    {
        services.TryAddSingleton<IArkCompositionExecutionJournal, ArkCompositionExecutionJournal>();
        services.TryAddSingleton<IArkCompositionExecutionLock, ArkCompositionPostgresExecutionLock>();
        services.TryAddSingleton<ArkComposedSwapExecutionService>();
        services.TryAddSingleton<ArkCompositionSourceEvidence>();
        services.TryAddSingleton<IArkCompositionPaymentSink, ArkCompositionPaymentSink>();
        services.AddHostedService<ArkCompositionExecutionHostedService>();
        return services;
    }
}
