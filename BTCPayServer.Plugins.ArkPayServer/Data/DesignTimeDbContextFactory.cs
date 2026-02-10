using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BTCPayServer.Plugins.ArkPayServer.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ArkPluginDbContext>
{
    public ArkPluginDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ArkPluginDbContext>();
        builder.UseNpgsql("User ID=postgres;Host=127.0.0.1;Port=39372;Database=designtimebtcpay");
        return new ArkPluginDbContext(builder.Options);
    }
}