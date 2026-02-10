using System.Globalization;
using System.Text;
using System.Web;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>
/// Helper class for building BIP21 URIs with Ark address support.
/// Supports the extended BIP21 format: bitcoin:[address]?amount=X&ark=Y&lightning=Z
/// </summary>
public class ArkadeBip21Builder
{
    private string? _onchainAddress;
    private string? _arkAddress;
    private string? _lightningInvoice;
    private decimal? _amount;
    private readonly Dictionary<string, string> _customParameters = new();

    /// <summary>
    /// Sets the Bitcoin onchain address (optional).
    /// </summary>
    public ArkadeBip21Builder WithOnchainAddress(string? address)
    {
        _onchainAddress = address;
        return this;
    }

    /// <summary>
    /// Sets the Ark address (required).
    /// </summary>
    public ArkadeBip21Builder WithArkAddress(string arkAddress)
    {
        if (string.IsNullOrWhiteSpace(arkAddress))
            throw new ArgumentException("Ark address cannot be null or empty", nameof(arkAddress));
        
        _arkAddress = arkAddress;
        return this;
    }

    /// <summary>
    /// Sets the Lightning invoice or LNURL (optional).
    /// </summary>
    public ArkadeBip21Builder WithLightning(string? lightning)
    {
        _lightningInvoice = lightning;
        return this;
    }

    /// <summary>
    /// Sets the payment amount in BTC (optional).
    /// </summary>
    public ArkadeBip21Builder WithAmount(decimal? amount)
    {
        _amount = amount;
        return this;
    }

    /// <summary>
    /// Adds a custom parameter to the BIP21 URI.
    /// </summary>
    public ArkadeBip21Builder WithCustomParameter(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Parameter key cannot be null or empty", nameof(key));
        
        _customParameters[key] = value;
        return this;
    }

    /// <summary>
    /// Builds the BIP21 URI string.
    /// Format: bitcoin:[onchain_address]?amount=X&ark=Y&lightning=Z
    /// </summary>
    public string Build()
    {
        if (string.IsNullOrWhiteSpace(_arkAddress))
            throw new InvalidOperationException("Ark address is required. Call WithArkAddress() before Build().");

        var sb = new StringBuilder("bitcoin:");
        
        // Add onchain address (or empty if not provided)
        sb.Append(_onchainAddress ?? string.Empty);
        
        // Start query parameters
        sb.Append('?');
        
        var parameters = new List<string>();
        
        // Add amount if provided
        if (_amount.HasValue)
        {
            parameters.Add($"amount={_amount.Value.ToString(CultureInfo.InvariantCulture)}");
        }
        
        // Add Ark address (always included)
        parameters.Add($"ark={HttpUtility.UrlEncode(_arkAddress)}");
        
        // Add lightning if provided
        if (!string.IsNullOrWhiteSpace(_lightningInvoice))
        {
            parameters.Add($"lightning={HttpUtility.UrlEncode(_lightningInvoice)}");
        }
        
        // Add custom parameters
        foreach (var (key, value) in _customParameters)
        {
            parameters.Add($"{HttpUtility.UrlEncode(key)}={HttpUtility.UrlEncode(value)}");
        }
        
        // Join all parameters
        sb.Append(string.Join("&", parameters));
        
        return sb.ToString();
    }

    /// <summary>
    /// Creates a new builder instance.
    /// </summary>
    public static ArkadeBip21Builder Create() => new();

    /// <summary>
    /// Parses a BIP21 URI and extracts the addresses and parameters.
    /// </summary>
    public static ArkadeBip21Info Parse(string bip21Uri)
    {
        if (string.IsNullOrWhiteSpace(bip21Uri))
            throw new ArgumentException("BIP21 URI cannot be null or empty", nameof(bip21Uri));

        if (!bip21Uri.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Invalid BIP21 URI: must start with 'bitcoin:'");

        var uri = new Uri(bip21Uri);
        var onchainAddress = uri.AbsolutePath.TrimStart('/');
        
        var query = HttpUtility.ParseQueryString(uri.Query);
        
        return new ArkadeBip21Info
        {
            OnchainAddress = string.IsNullOrWhiteSpace(onchainAddress) ? null : onchainAddress,
            ArkAddress = query["ark"],
            Lightning = query["lightning"],
            Amount = decimal.TryParse(query["amount"], NumberStyles.Any, CultureInfo.InvariantCulture, out var amount) 
                ? amount 
                : null
        };
    }
}

/// <summary>
/// Information extracted from a BIP21 URI with Ark support.
/// </summary>
public class ArkadeBip21Info
{
    public string? OnchainAddress { get; init; }
    public string? ArkAddress { get; init; }
    public string? Lightning { get; init; }
    public decimal? Amount { get; init; }
}
