using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>Store-scoped configuration input; RPC credentials have explicit write-only update semantics.</summary>
/// <param name="AssetId">Canonical CAIP-19 EIP-155 chain and ERC20 token identity.</param>
/// <param name="Destination">Merchant's nonzero EVM destination.</param>
/// <param name="Enabled">Requires complete configuration but does not enable SDK execution.</param>
/// <param name="RoutePolicy">Public safety and solver policy.</param>
/// <param name="RpcEndpoint">Omit to preserve the existing protected endpoint.</param>
/// <param name="ExpectedSenderAddress">Address the protected gas-payer key must derive.</param>
/// <param name="MaxFeePerGasWei">Maximum EIP-1559 fee per gas in decimal wei.</param>
/// <param name="MaxPriorityFeePerGasWei">Maximum EIP-1559 priority fee per gas in decimal wei.</param>
/// <param name="MaxGasLimit">Maximum transaction gas in decimal units.</param>
/// <param name="GasPayerPrivateKey">Omit to preserve the existing protected key.</param>
public sealed record ArkEvmSettlementUpdateData(string AssetId, string Destination, bool Enabled = false,
    ArkEvmRoutePolicy? RoutePolicy = null, ArkEvmRpcEndpointUpdate? RpcEndpoint = null,
    string? ExpectedSenderAddress = null, string? MaxFeePerGasWei = null,
    string? MaxPriorityFeePerGasWei = null, string? MaxGasLimit = null,
    ArkEvmGasPayerPrivateKeyUpdate? GasPayerPrivateKey = null);

/// <summary>Omission preserves the endpoint; replace supplies a URI, while clear removes it.</summary>
/// <param name="Action">preserve, replace or clear.</param>
/// <param name="Uri">Write-only HTTP(S) RPC URI, accepted only with replace.</param>
public sealed record ArkEvmRpcEndpointUpdate(string Action, string? Uri = null)
{
    /// <inheritdoc />
    public override string ToString() => nameof(ArkEvmRpcEndpointUpdate);
}

/// <summary>Omission preserves the key; replace supplies it, while clear removes it.</summary>
/// <param name="Action">preserve, replace or clear.</param>
/// <param name="PrivateKey">Write-only 32-byte hex private key, accepted only with replace.</param>
public sealed record ArkEvmGasPayerPrivateKeyUpdate(string Action, string? PrivateKey = null)
{
    /// <inheritdoc />
    public override string ToString() => nameof(ArkEvmGasPayerPrivateKeyUpdate);
}

/// <summary>Public settlement configuration without RPC credentials or ciphertext.</summary>
/// <param name="AssetId">Canonical CAIP-19 EIP-155 chain and ERC20 token identity.</param>
/// <param name="Destination">Merchant's EVM destination.</param>
/// <param name="Enabled">Whether settlement is requested by the store.</param>
/// <param name="RoutePolicy">Public safety and solver policy, absent for legacy configuration.</param>
/// <param name="RpcEndpointConfigured">Whether this store's protected RPC URI can be read.</param>
/// <param name="RpcEndpointOrigin">RPC scheme, host and port only.</param>
/// <param name="ExpectedSenderAddress">Configured gas-payer address.</param>
/// <param name="MaxFeePerGasWei">Maximum EIP-1559 fee per gas in decimal wei.</param>
/// <param name="MaxPriorityFeePerGasWei">Maximum EIP-1559 priority fee per gas in decimal wei.</param>
/// <param name="MaxGasLimit">Maximum transaction gas in decimal units.</param>
/// <param name="GasPayerConfigured">Whether the protected key is readable and matches its sender.</param>
/// <param name="MissingConfiguration">Nonsecret codes for missing prerequisites.</param>
public sealed record ArkEvmSettlementData(string AssetId, string Destination, bool Enabled, ArkEvmRoutePolicy? RoutePolicy,
    bool RpcEndpointConfigured, string? RpcEndpointOrigin, string? ExpectedSenderAddress,
    string? MaxFeePerGasWei, string? MaxPriorityFeePerGasWei, string? MaxGasLimit,
    bool GasPayerConfigured, string[] MissingConfiguration)
{
    /// <summary>Whether all configuration prerequisites, including the wallet, are present.</summary>
    public bool ConfigurationComplete => MissingConfiguration.Length == 0;
    /// <summary>Only final EVM settlement can complete a composed payment.</summary>
    public string PaymentCompletionCondition => "evm-settlement";
}
