namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>Merchant-selected bounds, independent of values offered by a solver.</summary>
public sealed record ArkEvmRoutePolicy
{
    /// <summary>Independently offered ARKADE, BTC-LN and/or BTC-CHAIN prompts.</summary>
    public string[] EnabledSourceRails { get; init; } = [];
    /// <summary>Selection for the Arkade-to-EVM quote.</summary>
    public ArkEvmSolverSelection? OutgoingSolver { get; init; }
    /// <summary>Selection for Lightning ingress when BTC-LN is enabled.</summary>
    public ArkEvmSolverSelection? LightningIngressSolver { get; init; }
    /// <summary>Selection for onchain ingress when BTC-CHAIN is enabled.</summary>
    public ArkEvmSolverSelection? OnchainIngressSolver { get; init; }
    /// <summary>Only this nonzero EVM swap contract may hold the route's token HTLC.</summary>
    public string SwapContractAddress { get; init; } = "";
    /// <summary>Fastest plausible EVM block cadence, retaining subsecond precision.</summary>
    public decimal FastestSecondsPerBlock { get; init; }
    /// <summary>Slowest plausible EVM block cadence, retaining subsecond precision.</summary>
    public decimal SlowestSecondsPerBlock { get; init; }
    /// <summary>Minimum EVM lock confirmation depth.</summary>
    public int MinConfirmations { get; init; }
    /// <summary>Minimum age in seconds of the EVM lock.</summary>
    public int MinAgeSeconds { get; init; }
    /// <summary>Required remaining EVM claim window in seconds.</summary>
    public int MinimumClaimWindowSeconds { get; init; }
    /// <summary>Safety margin in seconds before the Arkade refund deadline.</summary>
    public int ArkadeRefundMarginSeconds { get; init; }
    /// <summary>Requires signerless emulator refund support; false is currently unsupported.</summary>
    public bool RequireEmulatorRefundPath { get; init; } = true;

    /// <summary>Rejects incomplete or inconsistent policy and returns normalized public values.</summary>
    public ArkEvmRoutePolicy Validate()
    {
        if (EnabledSourceRails is not { Length: > 0 and <= 3 } ||
            EnabledSourceRails.Any(rail => rail is not ("ARKADE" or "BTC-LN" or "BTC-CHAIN")) ||
            EnabledSourceRails.Distinct(StringComparer.Ordinal).Count() != EnabledSourceRails.Length)
            throw new ArgumentException("Select distinct supported source payment methods.");
        if (OutgoingSolver is null || EnabledSourceRails.Contains("BTC-LN") && LightningIngressSolver is null ||
            EnabledSourceRails.Contains("BTC-CHAIN") && OnchainIngressSolver is null)
            throw new ArgumentException("Select solvers for all enabled route legs.");
        if (!ArkEvmSettlementSettings.IsNonzeroAddress(SwapContractAddress))
            throw new ArgumentException("Specify a nonzero EVM swap contract.");
        if (FastestSecondsPerBlock <= 0 || SlowestSecondsPerBlock < FastestSecondsPerBlock ||
            MinConfirmations <= 0 || MinAgeSeconds <= 0 || MinimumClaimWindowSeconds <= 0 || ArkadeRefundMarginSeconds <= 0)
            throw new ArgumentException("Specify positive confirmation, age, cadence and deadline bounds.");
        if (!RequireEmulatorRefundPath)
            throw new ArgumentException("Composed settlement requires the emulator refund path.");

        return this with
        {
            EnabledSourceRails = EnabledSourceRails.ToArray(),
            OutgoingSolver = OutgoingSolver.Validate(),
            LightningIngressSolver = LightningIngressSolver?.Validate(),
            OnchainIngressSolver = OnchainIngressSolver?.Validate(),
            SwapContractAddress = SwapContractAddress.ToLowerInvariant()
        };
    }
}
