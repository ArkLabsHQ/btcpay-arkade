using BTCPayServer.Lightning;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

public class ArkLightningConnectionStringHandler(IServiceProvider serviceProvider) : ILightningConnectionStringHandler
{
    public static string Build(string walletId, string? storeId) =>
        string.IsNullOrEmpty(storeId)
            ? $"type=arkade;wallet-id={walletId}"
            : $"type=arkade;wallet-id={walletId};store-id={storeId}";

    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        var kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
        if (type != "arkade")
        {
            error = "The key 'type' must be set to 'arkade' for ArkLightning connection strings";
            return null;
        }

        if (!kv.TryGetValue("wallet-id", out var walletId))
        {
            error = "The key 'wallet-id' is mandatory for ArkLightning connection strings";
            return null;
        }

        var storeId = kv.TryGetValue("store-id", out var sid) ? sid : "";

        error = null;
        return ActivatorUtilities.CreateInstance<ArkLightningClient>(serviceProvider, network, walletId, storeId);
    }
}

