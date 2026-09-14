namespace BTCPayServer.Plugins.ArkPayServer.Data;

/// <summary>Public quote facts already validated by the SDK; no claim packet or preimage is accepted.</summary>
/// <param name="RfqId">Prepared RFQ correlation id.</param>
/// <param name="PaymentHash">Route payment hash.</param>
/// <param name="SolverPubkey">Settlement x-only public key.</param>
/// <param name="FromAmount">Canonical integer atomic input amount.</param>
/// <param name="ToAmount">Canonical integer atomic output amount.</param>
/// <param name="LockupScript">L or M P2TR scriptPubKey.</param>
/// <param name="LockupAddress">Arkade address encoding that script.</param>
/// <param name="ValidUntil">Quote funding deadline, Unix seconds, when the SDK persisted it.</param>
/// <param name="RefundLocktime">Arkade refund deadline, Unix seconds.</param>
/// <param name="PayoutScript">Ingress non-interactive claim payout, exactly L.</param>
public sealed record ArkCompositionQuote(string RfqId, string PaymentHash, string SolverPubkey,
    string FromAmount, string ToAmount, string LockupScript, string LockupAddress, long? ValidUntil,
    long? RefundLocktime, string? PayoutScript = null);

/// <summary>Exact six-value ERC20Swap tuple plus the allowed contract that interprets it.</summary>
/// <param name="PaymentHash">Route SHA-256 hash.</param>
/// <param name="Amount">ERC20 atomic amount, uint256 decimal.</param>
/// <param name="TokenAddress">ERC20 contract.</param>
/// <param name="ClaimAddress">Merchant destination.</param>
/// <param name="RefundAddress">Expired EVM lock recipient.</param>
/// <param name="TimeoutBlock">EVM refund block height.</param>
/// <param name="SwapContractAddress">Configured ERC20Swap contract.</param>
public sealed record ArkCompositionEvmTerms(string PaymentHash, string Amount, string TokenAddress,
    string ClaimAddress, string RefundAddress, string TimeoutBlock, string SwapContractAddress);

/// <summary>Public coordinates of a lock proof performed by the SDK, not a substitute for verification.</summary>
/// <param name="TransactionId">Optional EVM funding transaction if available from status.</param>
/// <param name="ObservedAtBlock">Observed EVM tip.</param>
/// <param name="ProvenAtBlock">Historical block proving the lock.</param>
/// <param name="ProvenBlockTimestamp">Proving block timestamp, Unix seconds.</param>
public sealed record ArkCompositionEvmLockProof(string? TransactionId, string ObservedAtBlock,
    string ProvenAtBlock, long ProvenBlockTimestamp);

/// <summary>Fixed recovery facts; arbitrary remote error text is never persisted.</summary>
public enum ArkCompositionFailure
{
    /// <summary>A remote dependency must be retried or queried for status.</summary>
    RemoteUnavailable = 1,
    /// <summary>The prepared quote expired before execution.</summary>
    QuoteExpired,
    /// <summary>Observed funding did not meet the route contract.</summary>
    FundingRejected,
    /// <summary>Public EVM state did not meet the required proof.</summary>
    EvmProofRejected,
    /// <summary>Recovery must follow the persisted refund path.</summary>
    RefundRequired
}
