using System.Text.RegularExpressions;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>Explicit store identity; absent identity keeps legacy wallet-only behavior.</summary>
public sealed record ArkLightningStoreContext(string? StoreId)
{
    /// <summary>Accepts store identifiers without connection-string delimiters or control characters.</summary>
    public static bool IsValid(string storeId) => Regex.IsMatch(storeId, @"\A[A-Za-z0-9_-]{1,128}\z");
}
