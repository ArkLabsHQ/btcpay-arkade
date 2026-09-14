using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.ArkPayServer.Data;

/// <summary>Store-scoped persistence for the composition route index.</summary>
public sealed class ArkCompositionRouteRepository(IDbContextFactory<ArkPluginDbContext> factory)
{
    public async Task<ArkCompositionRoute?> Get(string storeId, Guid routeId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await Owned(context, storeId).SingleOrDefaultAsync(r => r.RouteId == routeId, cancellationToken);
    }

    public async Task<ArkCompositionRoute?> FindByPaymentHash(string storeId, string paymentHash,
        CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await Owned(context, storeId)
            .Where(r => r.PaymentHash == paymentHash.ToLowerInvariant())
            .OrderBy(r => r.RouteId).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ArkCompositionRoute>> List(string storeId, string? invoiceId = null,
        string? paymentMethodId = null, int skip = 0, int take = 100, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var query = Owned(context, storeId);
        if (invoiceId is not null) query = query.Where(r => r.InvoiceId == invoiceId);
        if (paymentMethodId is not null) query = query.Where(r => r.PaymentMethodId == paymentMethodId);
        return await query.OrderBy(r => r.RouteId)
            .Skip(skip).Take(take).AsNoTracking().ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ArkCompositionRoute>> ListAllWithPrompts(int skip, int take,
        CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await context.Set<ArkCompositionRoute>().AsNoTracking()
            .Where(r => r.CustomerDestination != null)
            .OrderBy(r => r.RouteId).Skip(skip).Take(take).ToArrayAsync(cancellationToken);
    }

    public async Task Add(string storeId, ArkCompositionRoute route, CancellationToken cancellationToken = default)
    {
        ValidateOwnership(storeId, route);
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        context.Set<ArkCompositionRoute>().Add(route);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task AttachInvoice(string storeId, Guid routeId, string invoiceId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var stored = await Owned(context, storeId).SingleOrDefaultAsync(r => r.RouteId == routeId, cancellationToken)
            ?? throw new InvalidOperationException("The composition route does not belong to this store.");
        stored.AttachInvoice(invoiceId);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static IQueryable<ArkCompositionRoute> Owned(ArkPluginDbContext context, string storeId) =>
        context.Set<ArkCompositionRoute>().Where(r => r.StoreId == storeId);

    private static void ValidateOwnership(string storeId, ArkCompositionRoute route)
    {
        if (route.StoreId != storeId || string.IsNullOrWhiteSpace(route.StoreId))
            throw new InvalidOperationException("The composition route does not belong to this store.");
    }
}
