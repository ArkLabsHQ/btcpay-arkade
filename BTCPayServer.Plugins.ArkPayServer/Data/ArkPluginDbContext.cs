using Microsoft.EntityFrameworkCore;
using NArk.Storage.EfCore;
using NArk.Storage.EfCore.Entities;

namespace BTCPayServer.Plugins.ArkPayServer.Data;

public class ArkPluginDbContext(DbContextOptions<ArkPluginDbContext> options) : DbContext(options)
{
    public DbSet<ArkWalletEntity> Wallets { get; set; }
    public DbSet<ArkWalletContractEntity> WalletContracts { get; set; }
    public DbSet<VtxoEntity> Vtxos { get; set; }
    public DbSet<ArkIntentEntity> Intents { get; set; }
    public DbSet<ArkIntentVtxoEntity> IntentVtxos { get; set; }
    
    public DbSet<ArkadeSwapIntentEntity> ArkadeIntentSwaps { get; set; }


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ConfigureArkEntities(opts =>
        {
            opts.Schema = "BTCPayServer.Plugins.Ark";
        });

        // Opt-in since the SDK split Arkade swap persistence into its own package: the core
        // mappings no longer carry ArkadeSwapIntents, so the table only exists if asked for.
        modelBuilder.ConfigureArkadeEntities(opts =>
        {
            opts.Schema = "BTCPayServer.Plugins.Ark";
        });
    }
}
