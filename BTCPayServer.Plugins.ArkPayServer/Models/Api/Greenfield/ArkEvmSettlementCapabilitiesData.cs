namespace BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;

/// <summary>Configuration readiness is independent of signer availability and SDK execution.</summary>
/// <param name="WalletConfigured">Whether the store has an Arkade wallet binding.</param>
/// <param name="SignerAvailable">Whether the wallet has a signer; not required for watch-only configuration.</param>
/// <param name="ConfigurationEnabled">Whether settlement is requested by the store.</param>
/// <param name="ConfigurationComplete">Whether all configuration prerequisites are present.</param>
/// <param name="EnabledSourceRails">Independently enabled payment methods.</param>
/// <param name="RpcEndpointConfigured">Whether this store's protected RPC URI can be read.</param>
/// <param name="RpcEndpointOrigin">RPC scheme, host and port only.</param>
/// <param name="GasPayerConfigured">Whether the protected key is readable and matches its sender.</param>
/// <param name="MissingConfiguration">Nonsecret codes for missing prerequisites.</param>
/// <param name="SdkCompositionAvailable">Whether the host registered the client-side composition executor.</param>
/// <param name="CrossProcessExecutionLockAvailable">Whether execution is protected across host processes.</param>
public sealed record ArkEvmSettlementCapabilitiesData(
    bool WalletConfigured, bool SignerAvailable, bool ConfigurationEnabled, bool ConfigurationComplete,
    string[] EnabledSourceRails, bool RpcEndpointConfigured, string? RpcEndpointOrigin,
    bool GasPayerConfigured, string[] MissingConfiguration, bool SdkCompositionAvailable,
    bool CrossProcessExecutionLockAvailable)
{
    /// <summary>Whether this store can create and execute client-composed routes.</summary>
    public bool ExecutionAvailable => ConfigurationEnabled && ConfigurationComplete && SdkCompositionAvailable && CrossProcessExecutionLockAvailable;
    /// <summary>Nonsecret reason execution is unavailable, or null when ready.</summary>
    public string? BlockedReason => !ConfigurationEnabled ? "configuration-disabled" :
        !ConfigurationComplete ? "configuration-incomplete" :
        !SdkCompositionAvailable ? "sdk-composition-unavailable" :
        !CrossProcessExecutionLockAvailable ? "cross-process-execution-lock-unavailable" : null;
    /// <summary>Ingress funding alone cannot complete a composed payment.</summary>
    public string PaymentCompletionCondition => "evm-settlement";
}
