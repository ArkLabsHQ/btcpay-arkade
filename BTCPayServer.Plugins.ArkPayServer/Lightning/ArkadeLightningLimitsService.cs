using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Caching.Memory;
using NBXplorer;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>
/// Decides whether a store can be offered Lightning at all.
/// </summary>
/// <remarks>
/// <para>
/// Two questions, and only the second is interesting. Whether a store is on Arkade Lightning is
/// read from its connection string; whether Arkade Lightning can currently settle anything is
/// whether a solver is configured to settle it with.
/// </para>
/// <para>
/// It deliberately does not pre-check the amount. The Boltz integration this replaced could, because
/// Boltz published its limits as an endpoint; an Arkade solver's terms are per-quote, and the only
/// way to learn them is to open a negotiation — far too expensive to do while rendering a checkout
/// page, and stale by the time the customer pays. An amount a solver will not take is refused at the
/// point of quoting, with the solver's own reason attached, which is a better error than a guess made
/// minutes earlier.
/// </para>
/// </remarks>
public class ArkadeLightningLimitsService : IDisposable
{
    private readonly ArkadeSolverService _solver;
    private readonly PaymentMethodHandlerDictionary _paymentMethodHandlerDictionary;
    private readonly IMemoryCache _memoryCache;
    private readonly StoreRepository _storeRepository;
    private readonly CompositeDisposable _leases = new();

    private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(5);

    public ArkadeLightningLimitsService(
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

        // Subscribe to store update events to automatically clear cache
        _leases.Add(eventAggregator.Subscribe<StoreEvent.Updated>(ev =>
        {
            ClearStoreCache(ev.StoreId);
        }));
    }
    
    private static string GetStoreCacheKey(string storeId) => $"arkade-lightning-{storeId}";

    /// <summary>
    /// Checks if a store uses Arkade Lightning connection
    /// </summary>
    public bool IsStoreUsingArkadeLightning(StoreData store)
    {
        if (store?.Id == null)
            return false;

        // Use IMemoryCache with automatic expiry
        return _memoryCache.GetOrCreate<bool?>(GetStoreCacheKey(store.Id), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheExpiry;
            
            // Check if store has Arkade Lightning configured
            var lnPaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");
            var lnConfig = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(
                lnPaymentMethodId,
                _paymentMethodHandlerDictionary);
            
            return lnConfig?.ConnectionString?.StartsWith("type=arkade", StringComparison.InvariantCultureIgnoreCase) is true;
        }) ?? false;
    }

    /// <summary>
    /// Determines if Lightning should be supported for a given store ID and amount
    /// </summary>
    /// <param name="storeId">The store ID</param>
    /// <param name="amountSats">Amount in satoshis (0 for top-up invoices)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if Lightning should be included, false otherwise</returns>
    public Task<bool> CanSupportLightningAsync(string storeId, long amountSats,
        CancellationToken cancellationToken = default)
    {
        // Allow top-up invoices (amount = 0)
        if (amountSats == 0)
            return Task.FromResult(true);

        // Check cache first to see if store uses Arkade Lightning
        var isArkade = _memoryCache.GetOrCreate<bool?>(GetStoreCacheKey(storeId), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheExpiry;
            
            // Need to fetch store to check configuration
            var store = _storeRepository.FindStore(storeId).GetAwaiter().GetResult();
            if (store == null)
                return null;
            
            // Check if store has Arkade Lightning configured
            var lnPaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");
            var lnConfig = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(
                lnPaymentMethodId,
                _paymentMethodHandlerDictionary);
            
            return lnConfig?.ConnectionString?.StartsWith("type=arkade", StringComparison.InvariantCultureIgnoreCase) is true;
        });
        
        // If store doesn't use Arkade Lightning, always allow Lightning
        if (isArkade != true)
        {
            return Task.FromResult(true);
        }

        // No solver means nothing can settle a Lightning payment for this store, so offering the
        // method would hand the customer an invoice that cannot be fulfilled.
        return Task.FromResult(_solver.IsConfigured);
    }

    /// <summary>
    /// Determines if Lightning should be supported for a given store and amount
    /// </summary>
    /// <param name="store">The store data</param>
    /// <param name="amountSats">Amount in satoshis (0 for top-up invoices)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if Lightning should be included, false otherwise</returns>
    public Task<bool> CanSupportLightningAsync(StoreData? store, long amountSats, CancellationToken cancellationToken = default)
    {
        // Allow top-up invoices (amount = 0)
        if (amountSats == 0)
            return Task.FromResult(true);

        // If store doesn't use Arkade Lightning, always allow Lightning
        if (store == null || !IsStoreUsingArkadeLightning(store))
        {
            return Task.FromResult(true);
        }

        return Task.FromResult(_solver.IsConfigured);
    }

    /// <summary>
    /// Clears the cache for a specific store (called automatically when store is updated)
    /// </summary>
    public void ClearStoreCache(string storeId)
    {
        _memoryCache.Remove(GetStoreCacheKey(storeId));
    }

    /// <summary>
    /// Disposes event subscriptions
    /// </summary>
    public void Dispose()
    {
        _leases?.Dispose();
    }
}
