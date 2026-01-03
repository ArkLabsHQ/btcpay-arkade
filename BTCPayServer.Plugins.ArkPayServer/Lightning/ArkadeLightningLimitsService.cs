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
/// Service that determines if a store uses Arkade Lightning and validates amounts against Boltz limits
/// </summary>
public class ArkadeLightningLimitsService : IDisposable
{
    private readonly BoltzLimitsService? _boltzLimitsService;
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
        BoltzLimitsService? boltzLimitsService = null)
    {
        _boltzLimitsService = boltzLimitsService;
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
    public async Task<bool> CanSupportLightningAsync(string storeId, long amountSats,
        CancellationToken cancellationToken = default)
    {
        // Allow top-up invoices (amount = 0)
        if (amountSats == 0)
            return true;
        
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
            return true;
        }

        // If BoltzLimitsService is not available, disallow Lightning for Arkade stores
        if (_boltzLimitsService == null)
        {
            return false;
        }

        // Validate against Boltz limits
        try
        {
            var (isValid, _) = await _boltzLimitsService.ValidateAmountAsync(amountSats, isReverse: true, cancellationToken);
            return isValid;
        }
        catch (Exception)
        {
            // If we can't validate (e.g., Boltz unavailable), be conservative and disallow
            return false;
        }
    }
    
    /// <summary>
    /// Determines if Lightning should be supported for a given store and amount
    /// </summary>
    /// <param name="store">The store data</param>
    /// <param name="amountSats">Amount in satoshis (0 for top-up invoices)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if Lightning should be included, false otherwise</returns>
    public async Task<bool> CanSupportLightningAsync(StoreData? store, long amountSats, CancellationToken cancellationToken = default)
    {
        // Allow top-up invoices (amount = 0)
        if (amountSats == 0)
            return true;
            
        // If store doesn't use Arkade Lightning, always allow Lightning
        if (store == null || !IsStoreUsingArkadeLightning(store))
        {
            return true;
        }

        // If BoltzLimitsService is not available, disallow Lightning for Arkade stores
        // since we can't fulfill Lightning payments without Boltz
        if (_boltzLimitsService == null)
        {
            return false;
        }

        // Validate against Boltz limits
        try
        {
            var (isValid, _) = await _boltzLimitsService.ValidateAmountAsync(amountSats, isReverse: true, cancellationToken);
            return isValid;
        }
        catch (Exception)
        {
            // If we can't validate (e.g., Boltz unavailable), be conservative and disallow
            // This prevents creating invoices that can't be fulfilled
            return false;
        }
    }

    /// <summary>
    /// Gets Boltz limits if the store uses Arkade Lightning, otherwise returns null
    /// </summary>
    public async Task<BoltzLimitsCache?> GetLimitsForStoreAsync(StoreData? store, CancellationToken cancellationToken = default)
    {
        if (store == null || !IsStoreUsingArkadeLightning(store))
        {
            return null;
        }

        if (_boltzLimitsService == null)
        {
            return null;
        }

        try
        {
            return await _boltzLimitsService.GetLimitsAsync(cancellationToken);
        }
        catch (Exception)
        {
            return null;
        }
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
