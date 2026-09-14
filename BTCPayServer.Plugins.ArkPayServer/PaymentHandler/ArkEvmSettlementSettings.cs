using System.Numerics;
using System.Text.RegularExpressions;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>Persisted merchant settlement policy; never return this storage record from an API.</summary>
/// <param name="AssetId">Canonical CAIP-19 source of the EIP-155 chain and ERC20 token identity.</param>
/// <param name="Destination">Merchant's nonzero EVM destination.</param>
/// <param name="Enabled">Whether settlement is requested; execution additionally requires SDK support.</param>
public sealed record ArkEvmSettlementSettings(string AssetId, string Destination, bool Enabled = false)
{
    /// <summary>Explicit route bounds; absent on legacy settings, which cannot enable execution.</summary>
    public ArkEvmRoutePolicy? RoutePolicy { get; init; }
    /// <summary>Store-bound Data Protection ciphertext persisted by the configuration serializer, never an API field.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ProtectedRpcUri { get; init; }
    /// <summary>Store-and-address-bound Data Protection ciphertext, never an API field.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ProtectedGasPayerPrivateKey { get; init; }
    /// <summary>Lowercase address the protected gas-payer key must derive.</summary>
    public string? ExpectedSenderAddress { get; init; }
    /// <summary>Maximum accepted EIP-1559 fee per gas in canonical decimal wei.</summary>
    public string? MaxFeePerGasWei { get; init; }
    /// <summary>Maximum accepted EIP-1559 priority fee per gas in canonical decimal wei.</summary>
    public string? MaxPriorityFeePerGasWei { get; init; }
    /// <summary>Maximum transaction gas in canonical decimal units.</summary>
    public string? MaxGasLimit { get; init; }

    private static readonly Regex AssetPattern = new(
        "\\Aeip155:[1-9][0-9]{0,31}/erc20:0x[0-9a-fA-F]{40}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex AddressPattern = new("\\A0x[0-9a-fA-F]{40}\\z", RegexOptions.CultureInvariant);
    private const string ZeroAddress = "0x0000000000000000000000000000000000000000";

    /// <summary>Validates public values while preserving policy-absent legacy records for reading.</summary>
    public ArkEvmSettlementSettings Validate()
    {
        if (AssetId is null || !AssetPattern.IsMatch(AssetId) || AssetId.EndsWith(ZeroAddress, StringComparison.Ordinal))
            throw new ArgumentException("Settlement asset must identify a nonzero ERC20 contract and positive EVM chain.");
        if (!IsNonzeroAddress(Destination))
            throw new ArgumentException("Settlement destination must be a nonzero EVM address.");
        if (ExpectedSenderAddress is not null && !IsNonzeroAddress(ExpectedSenderAddress))
            throw new ArgumentException("Settlement gas payer must be a nonzero EVM address.");
        var maxFee = ValidateQuantity(MaxFeePerGasWei);
        var maxPriorityFee = ValidateQuantity(MaxPriorityFeePerGasWei);
        ValidateQuantity(MaxGasLimit);
        if (maxFee is not null && maxPriorityFee > maxFee)
            throw new ArgumentException("Settlement priority fee cannot exceed the maximum fee.");

        return this with
        {
            AssetId = AssetId.ToLowerInvariant(),
            Destination = Destination.ToLowerInvariant(),
            ExpectedSenderAddress = ExpectedSenderAddress?.ToLowerInvariant(),
            RoutePolicy = RoutePolicy?.Validate()
        };
    }

    internal static bool IsNonzeroAddress(string? value) =>
        value is not null && AddressPattern.IsMatch(value) && value != ZeroAddress;

    private static BigInteger? ValidateQuantity(string? value)
    {
        if (value is null) return null;
        if (value.Length is < 1 or > 78 || value[0] is < '1' or > '9' || value.Any(character => !char.IsAsciiDigit(character)) ||
            !BigInteger.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) || parsed >= (BigInteger.One << 256))
            throw new ArgumentException("Settlement EVM quantities must be positive canonical uint256 decimal strings.");
        return parsed;
    }

    /// <inheritdoc />
    public override string ToString() => nameof(ArkEvmSettlementSettings);
}
