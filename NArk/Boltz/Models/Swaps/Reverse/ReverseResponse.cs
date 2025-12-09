using System.Text.Json.Serialization;

namespace NArk.Boltz.Models.Swaps.Reverse;

public class ReverseResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; }
    
    [JsonPropertyName("lockupAddress")]
    public string LockupAddress { get; set; }
    
    [JsonPropertyName("refundPublicKey")]
    public string RefundPublicKey { get; set; }
    
    [JsonPropertyName("timeoutBlockHeights")]
    public TimeoutBlockHeights TimeoutBlockHeights { get; set; }
    
    [JsonPropertyName("invoice")]
    public string Invoice { get; set; }

 
}