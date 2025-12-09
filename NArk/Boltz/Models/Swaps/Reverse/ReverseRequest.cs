using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Reverse;

public class ReverseRequest
{
    [JsonPropertyName("from")]
    public string From { get; set; } // e.g., "LNBTC"

    [JsonPropertyName("to")]
    public string To { get; set; } // e.g., "BTC"

    [JsonPropertyName("onchainAddress")]
    public string OnchainAddress { get; set; }

    [JsonPropertyName("onchainAmount")]
    public long? OnchainAmount { get; set; }

    [JsonPropertyName("invoiceAmount")]
    public long? InvoiceAmount { get; set; }

    [JsonPropertyName("preimageHash")]
    public string PreimageHash { get; set; }

    [JsonPropertyName("claimPublicKey")]
    public string? ClaimPublicKey { get; set; } // For Taproot

    [JsonPropertyName("referralId")]
    public string? ReferralId { get; set; }

    [JsonPropertyName("acceptZeroConf")]
    public bool? AcceptZeroConf { get; set; }

    [JsonPropertyName("invoiceExpiry")]
    public int? InvoiceExpirySeconds { get; set; }
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    [JsonPropertyName("descriptionHash")]
    public string? DescriptionHash { get; set; }
    
}
