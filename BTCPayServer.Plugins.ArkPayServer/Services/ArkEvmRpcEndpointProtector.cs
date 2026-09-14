using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>RPC credentials are encrypted with the deployment key ring and bound to their store.</summary>
public sealed class ArkEvmRpcEndpointProtector(IDataProtectionProvider provider)
{
    /// <summary>Validates and encrypts an RPC URI under the owning store's protection purpose.</summary>
    public string Protect(string storeId, string? endpoint) => ForStore(storeId).Protect(Validate(endpoint).AbsoluteUri);

    /// <summary>Returns the URI only for its owning store and available deployment key; otherwise null.</summary>
    public Uri? TryUnprotect(string storeId, string? protectedEndpoint)
    {
        if (string.IsNullOrEmpty(protectedEndpoint)) return null;
        try
        {
            return Validate(ForStore(storeId).Unprotect(protectedEndpoint));
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private IDataProtector ForStore(string storeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeId);
        return provider.CreateProtector("BTCPayServer.Plugins.ArkPayServer.EvmRpc.v1", storeId);
    }

    private static Uri Validate(string? endpoint)
    {
        if (endpoint is null || endpoint.Length > 4096 || endpoint.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host) || uri.Fragment.Length != 0)
            throw new ArgumentException("Specify an absolute HTTP(S) RPC endpoint without a fragment.");
        return uri;
    }
}
