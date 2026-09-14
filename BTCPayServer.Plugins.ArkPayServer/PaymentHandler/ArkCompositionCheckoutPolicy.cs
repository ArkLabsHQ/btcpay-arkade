using System.Collections.Concurrent;
using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public sealed class ArkCompositionCheckoutPolicy(IArkCompositionContextSource contextSource,
    ArkCompositionRouteRepository repository, IMemoryCache cache, TimeProvider? timeProvider = null,
    ILogger<ArkCompositionCheckoutPolicy>? logger = null)
{
    private readonly ConcurrentDictionary<string, Lazy<Task>> _preloads = new(StringComparer.Ordinal);

    public bool RequiresComposition(InvoiceEntity invoice, StoreData? store = null)
    {
        if (store is not null) RememberStore(store);
        if (invoice.GetPaymentPrompts().Any(p => p.Details?["compositionRouteId"] is not null)) return true;
        return cache.TryGetValue(StoreKey(invoice.StoreId), out bool enabled) && enabled;
    }

    public bool CanInclude(PaymentPrompt? prompt, bool requiresComposition)
    {
        if (prompt is not { Activated: true, Destination: not null, Details: not null }) return false;
        if (!requiresComposition) return true;
        var id = prompt.Details.Value<string>("compositionRouteId") ?? prompt.Details.Value<string>("invoiceId");
        if (!Guid.TryParse(id, out var routeId)) return false;
        if (!cache.TryGetValue(RouteKey(prompt.ParentEntity.StoreId, routeId), out RouteBinding? route))
        {
            BeginPreload(prompt.ParentEntity);
            return false;
        }
        return route.PaymentMethodId == prompt.PaymentMethodId.ToString() &&
            (route.InvoiceId == prompt.ParentEntity.Id || route.PaymentMethodId == "BTC-LN" && route.InvoiceId is null) &&
            route.CustomerDestination == prompt.Destination && route.PaymentHash == prompt.Details.Value<string>("paymentHash") &&
            route.CheckoutExpiresAt > (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
    }

    public async Task PreloadAsync(InvoiceEntity invoice, StoreData? store = null,
        CancellationToken cancellationToken = default)
    {
        store ??= await contextSource.FindStoreAsync(invoice.StoreId);
        if (store is not null) RememberStore(store);
        foreach (var prompt in invoice.GetPaymentPrompts())
        {
            var id = prompt.Details?.Value<string>("compositionRouteId") ??
                prompt.Details?.Value<string>("invoiceId");
            if (!Guid.TryParse(id, out var routeId)) continue;
            var route = await repository.Get(invoice.StoreId, routeId, cancellationToken);
            if (route is not null) RememberRoute(route);
        }
    }

    public void BeginPreload(InvoiceEntity invoice, StoreData? store = null)
    {
        if (store is not null) RememberStore(store);
        var key = $"{invoice.StoreId}:{invoice.Id}";
        var preload = _preloads.GetOrAdd(key, unused => new Lazy<Task>(async () =>
        {
            try { await PreloadAsync(invoice, store); }
            catch (Exception error)
            {
                logger?.LogWarning(error, "Could not preload composed checkout bindings for invoice {InvoiceId}",
                    invoice.Id);
            }
            finally { _preloads.TryRemove(key, out _); }
        }, LazyThreadSafetyMode.ExecutionAndPublication));
        _ = preload.Value;
    }

    public void RememberStore(StoreData store)
    {
        var enabled = ArkCompositionPromptService.Configuration(store)?.EvmSettlement?.Enabled == true;
        cache.Set(StoreKey(store.Id), enabled, TimeSpan.FromMinutes(10));
    }

    public void RememberRoute(ArkCompositionRoute route)
    {
        if (route.CheckoutExpiresAt is not { } expiresAt || route.CustomerDestination is null)
            return;
        var expiry = DateTimeOffset.FromUnixTimeSeconds(expiresAt);
        if (expiry <= (timeProvider ?? TimeProvider.System).GetUtcNow()) return;
        cache.Set(RouteKey(route.StoreId, route.RouteId), new RouteBinding(route.PaymentMethodId, route.InvoiceId,
            route.CustomerDestination, route.PaymentHash, expiresAt), expiry);
    }

    private static string StoreKey(string storeId) => $"ark-composition-checkout:store:{storeId}";
    private static string RouteKey(string storeId, Guid routeId) => $"ark-composition-checkout:route:{storeId}:{routeId:N}";

    private sealed record RouteBinding(string PaymentMethodId, string? InvoiceId, string CustomerDestination,
        string PaymentHash, long CheckoutExpiresAt);
}
