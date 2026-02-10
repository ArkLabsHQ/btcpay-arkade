namespace BTCPayServer.Plugins.Arkade.Helpers;

/// <summary>
/// Shared helper methods for Ark views
/// </summary>
public static class ArkViewHelpers
{
    /// <summary>
    /// Checks if the search contains a filter with the specified type and optional key
    /// </summary>
    public static bool HasArrayFilter(SearchString? search, string type, string? key = null) =>
        search?.ContainsFilter(type) == true &&
        (key is null || search.GetFilterArray(type).Contains(key));

    /// <summary>
    /// Gets the count of active filters for a specific filter type
    /// </summary>
    public static int GetFilterCount(SearchString? search, string filterType) =>
        search?.ContainsFilter(filterType) == true ? search.GetFilterArray(filterType).Length : 0;
}
