using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Caching.Memory;
using NBXplorer;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

// No amount pre-check: solver terms are per quote, and an amount it declines is refused at quoting with its reason.
public class ArkadeLightningAvailabilityService : IDisposable
{
    private readonly ArkadeSolverService _solver;
    private readonly PaymentMethodHandlerDictionary _paymentMethodHandlerDictionary;
    private readonly IMemoryCache _memoryCache;
    private readonly StoreRepository _storeRepository;
    private readonly CompositeDisposable _leases = new();

    private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(5);

    public ArkadeLightningAvailabilityService(
        PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
        EventAggregator eventAggregator,
        IMemoryCache memoryCache,
        StoreRepository storeRepository,
        ArkadeSolverService solver)
    {
        _solver = solver;
        _paymentMethodHandlerDictionary = paymentMethodHandlerDictionary;
        _memoryCache = memoryCache;
        _storeRepository = storeRepository;

        _leases.Add(eventAggregator.Subscribe<StoreEvent.Updated>(ev => ClearStoreCache(ev.StoreId)));
    }

    private static string GetStoreCacheKey(string storeId) => $"arkade-lightning-{storeId}";

    public bool IsStoreUsingArkadeLightning(StoreData? store)
    {
        if (store?.Id is not { } storeId)
        {
            return false;
        }

        if (_memoryCache.TryGetValue<bool>(GetStoreCacheKey(storeId), out var cached))
        {
            return cached;
        }

        var usesArkade = UsesArkadeLightning(store);
        _memoryCache.Set(GetStoreCacheKey(storeId), usesArkade, CacheExpiry);
        return usesArkade;
    }

    public async Task<bool> ShouldOfferLightningAsync(
        string storeId, CancellationToken cancellationToken = default)
    {
        if (!await IsStoreUsingArkadeLightningAsync(storeId, cancellationToken))
        {
            return true;
        }

        return await _solver.HasSolverAsync(cancellationToken);
    }

    public void ClearStoreCache(string storeId) => _memoryCache.Remove(GetStoreCacheKey(storeId));

    public void Dispose() => _leases.Dispose();

    private async Task<bool> IsStoreUsingArkadeLightningAsync(
        string storeId, CancellationToken cancellationToken)
    {
        var key = GetStoreCacheKey(storeId);
        if (_memoryCache.TryGetValue<bool>(key, out var cached))
        {
            return cached;
        }

        // FindStore takes no token, so honour cancellation before the DB round-trip.
        cancellationToken.ThrowIfCancellationRequested();

        var store = await _storeRepository.FindStore(storeId);
        var usesArkade = store is not null && UsesArkadeLightning(store);

        _memoryCache.Set(key, usesArkade, CacheExpiry);
        return usesArkade;
    }

    private bool UsesArkadeLightning(StoreData store)
    {
        var lnPaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var lnConfig = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(
            lnPaymentMethodId,
            _paymentMethodHandlerDictionary);

        return lnConfig?.ConnectionString?.StartsWith(
            "type=arkade", StringComparison.InvariantCultureIgnoreCase) is true;
    }
}
