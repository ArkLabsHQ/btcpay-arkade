using Microsoft.Extensions.Logging;
using NArk.Swaps.Boltz.Client;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>
/// Service for caching and validating Boltz swap limits.
/// This is a lightweight service that handles only limits/fees validation.
/// Actual swap management is handled by NNark's SwapsManagementService.
/// </summary>
public class BoltzLimitsService(
    BoltzClient boltzClient,
    ILogger<BoltzLimitsService> logger)
{
    private BoltzLimitsCache? _limitsCache;
    private readonly SemaphoreSlim _limitsCacheLock = new(1, 1);
    private static readonly TimeSpan LimitsCacheExpiry = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Gets cached Boltz limits, fetching from API if cache is expired or empty
    /// </summary>
    public async Task<BoltzLimitsCache> GetLimitsAsync(CancellationToken cancellationToken = default)
    {
        await _limitsCacheLock.WaitAsync(cancellationToken);
        try
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

            // Return cached limits if still valid
            if (_limitsCache != null && DateTimeOffset.UtcNow < _limitsCache.ExpiresAt)
            {
                return _limitsCache;
            }
            _limitsCache = null;

            // Fetch fresh limits from Boltz API
            try
            {
                var submarinePairsTask = boltzClient.GetSubmarinePairsAsync(cts.Token);
                var reversePairsTask = boltzClient.GetReversePairsAsync(cts.Token);

                await Task.WhenAll(submarinePairsTask, reversePairsTask);

                var submarinePairs = await submarinePairsTask;
                var reversePairs = await reversePairsTask;

                if (submarinePairs?.ARK?.BTC != null && reversePairs?.BTC?.ARK != null)
                {
                    _limitsCache = new BoltzLimitsCache
                    {
                        // Submarine: Ark → Lightning (sending)
                        SubmarineMinAmount = submarinePairs.ARK.BTC.Limits?.Minimal ?? 0,
                        SubmarineMaxAmount = submarinePairs.ARK.BTC.Limits?.Maximal ?? long.MaxValue,
                        // Boltz API returns percentage as 0.01 for 0.01%, so divide by 100 to get decimal multiplier
                        SubmarineFeePercentage = (submarinePairs.ARK.BTC.Fees?.Percentage ?? 0) / 100m,
                        SubmarineMinerFee = submarinePairs.ARK.BTC.Fees?.MinerFeesValue ?? 0,

                        // Reverse: Lightning → Ark (receiving)
                        ReverseMinAmount = reversePairs.BTC.ARK.Limits?.Minimal ?? 0,
                        ReverseMaxAmount = reversePairs.BTC.ARK.Limits?.Maximal ?? long.MaxValue,
                        // Boltz API returns percentage as 0.01 for 0.01%, so divide by 100 to get decimal multiplier
                        ReverseFeePercentage = (reversePairs.BTC.ARK.Fees?.Percentage ?? 0) / 100m,
                        ReverseMinerFee = reversePairs.BTC.ARK.Fees?.MinerFees?.Claim ?? 0,

                        FetchedAt = DateTimeOffset.UtcNow,
                        ExpiresAt = DateTimeOffset.UtcNow.Add(LimitsCacheExpiry)
                    };

                    logger.LogInformation("Fetched Boltz limits - Submarine: {SubMin}-{SubMax} sats, Reverse: {RevMin}-{RevMax} sats",
                        _limitsCache.SubmarineMinAmount, _limitsCache.SubmarineMaxAmount,
                        _limitsCache.ReverseMinAmount, _limitsCache.ReverseMaxAmount);

                    return _limitsCache;
                }
                else
                {
                    throw new InvalidOperationException("Boltz instance does not support Ark");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to fetch Boltz limits");
                throw new InvalidOperationException("Failed to fetch Boltz limits");
            }
        }
        finally
        {
            _limitsCacheLock.Release();
        }
    }

    /// <summary>
    /// Validates if an amount is within Boltz limits for the specified swap type
    /// </summary>
    public async Task<(bool IsValid, string? ErrorMessage)> ValidateAmountAsync(long amountSats, bool isReverse, CancellationToken cancellationToken = default)
    {
        var limits = await GetLimitsAsync(cancellationToken);

        var (minAmount, maxAmount, swapType) = isReverse
            ? (limits.ReverseMinAmount, limits.ReverseMaxAmount, "receiving")
            : (limits.SubmarineMinAmount, limits.SubmarineMaxAmount, "sending");

        if (amountSats < minAmount)
        {
            return (false, $"Amount {amountSats} sats is below minimum {minAmount} sats for {swapType} Lightning");
        }

        if (amountSats > maxAmount)
        {
            return (false, $"Amount {amountSats} sats exceeds maximum {maxAmount} sats for {swapType} Lightning");
        }

        return (true, null);
    }

    /// <summary>
    /// Validates if the actual swap fee is within acceptable range compared to expected fee
    /// </summary>
    /// <param name="amountSats">The invoice/payment amount in satoshis</param>
    /// <param name="actualSwapAmount">The actual onchain/expected amount from Boltz</param>
    /// <param name="isReverse">True for reverse swap (Lightning → Ark), false for submarine (Ark → Lightning)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple indicating if fees are valid and optional error message</returns>
    public async Task<(bool IsValid, string? ErrorMessage)> ValidateFeesAsync(long amountSats, long actualSwapAmount, bool isReverse, CancellationToken cancellationToken = default)
    {
        var limits = await GetLimitsAsync(cancellationToken);

        // Calculate actual fee based on swap type
        // Reverse: user receives actualSwapAmount onchain, pays amountSats via Lightning
        // Submarine: user pays actualSwapAmount onchain, receives amountSats via Lightning
        var actualFee = isReverse
            ? amountSats - actualSwapAmount  // Reverse: Lightning amount - onchain amount
            : actualSwapAmount - amountSats; // Submarine: onchain amount - Lightning amount

        var (feePercentage, minerFee, swapType) = isReverse
            ? (limits.ReverseFeePercentage, limits.ReverseMinerFee, "Reverse")
            : (limits.SubmarineFeePercentage, limits.SubmarineMinerFee, "Submarine");

        // Calculate expected fee: (amount × percentage) + miner fee
        var expectedFee = (long)(amountSats * feePercentage) + minerFee;
        var feeToleranceSats = 100; // Allow 100 sat tolerance for rounding

        // Only fail if actual fee is HIGHER than expected (allow lower fees)
        if (actualFee > expectedFee + feeToleranceSats)
        {
            logger.LogWarning("{SwapType} swap fee too high: expected ~{ExpectedFee} sats ({FeePercentage}% + {MinerFee} sats miner fee), got {ActualFee} sats",
                swapType, expectedFee, feePercentage * 100, minerFee, actualFee);
            return (false,
                $"Boltz fee verification failed. Expected ~{expectedFee} sats ({feePercentage * 100:F2}% + {minerFee} sats miner fee), but swap would charge {actualFee} sats");
        }

        if (actualFee < expectedFee - feeToleranceSats)
        {
            logger.LogInformation("{SwapType} swap fee lower than expected: {ActualFee} sats vs expected {ExpectedFee} sats - accepting",
                swapType, actualFee, expectedFee);
        }

        logger.LogInformation("{SwapType} swap fee verified: {ActualFee} sats ({FeePercentage}% + {MinerFee} sats miner fee)",
            swapType, actualFee, feePercentage * 100, minerFee);

        return (true, null);
    }
}

public class BoltzLimitsCache
{
    // Submarine swap limits (Ark → Lightning, sending)
    public long SubmarineMinAmount { get; set; }
    public long SubmarineMaxAmount { get; set; }
    public decimal SubmarineFeePercentage { get; set; }
    public long SubmarineMinerFee { get; set; }

    // Reverse swap limits (Lightning → Ark, receiving)
    public long ReverseMinAmount { get; set; }
    public long ReverseMaxAmount { get; set; }
    public decimal ReverseFeePercentage { get; set; }
    public long ReverseMinerFee { get; set; }

    public DateTimeOffset FetchedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
