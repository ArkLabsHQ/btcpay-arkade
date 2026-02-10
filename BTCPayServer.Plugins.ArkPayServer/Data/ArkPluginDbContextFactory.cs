using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.ArkPayServer.Data;

public class ArkPluginDbContextFactory(IOptions<DatabaseOptions> options) : BaseDbContextFactory<ArkPluginDbContext>(options, "BTCPayServer.Plugins.Ark")
{
    public override ArkPluginDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
    {
        var builder = new DbContextOptionsBuilder<ArkPluginDbContext>();
        ConfigureBuilder(builder);
        return new ArkPluginDbContext(builder.Options);
    }
}