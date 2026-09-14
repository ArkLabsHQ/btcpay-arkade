using Microsoft.EntityFrameworkCore;
using NArk.Storage.EfCore;
using NArk.Storage.EfCore.Entities;
using BTCPayServer.Plugins.ArkPayServer.Data.Legacy;

namespace BTCPayServer.Plugins.ArkPayServer.Data;

public class ArkPluginDbContext(DbContextOptions<ArkPluginDbContext> options) : DbContext(options)
{
    public DbSet<ArkWalletEntity> Wallets { get; set; }
    public DbSet<ArkWalletContractEntity> WalletContracts { get; set; }
    public DbSet<VtxoEntity> Vtxos { get; set; }
    public DbSet<ArkIntentEntity> Intents { get; set; }
    public DbSet<ArkIntentVtxoEntity> IntentVtxos { get; set; }

    public DbSet<LegacySwap> Swaps { get; set; }
    public DbSet<ArkadeSwapIntentEntity> ArkadeIntentSwaps { get; set; }
    /// <summary>Store-scoped index from invoices to SDK-composed routes. Holds no swap state.</summary>
    public DbSet<ArkCompositionRoute> CompositionRoutes { get; set; }


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ConfigureArkEntities(opts =>
        {
            opts.Schema = "BTCPayServer.Plugins.Ark";
        });
        modelBuilder.ConfigureArkadeEntities(opts => opts.Schema = "BTCPayServer.Plugins.Ark");
        LegacySwap.Configure(modelBuilder.Entity<LegacySwap>());
        modelBuilder.Entity<ArkCompositionRoute>(entity =>
        {
            entity.ToTable("CompositionRoutes", "BTCPayServer.Plugins.Ark");
            entity.HasKey(c => c.RouteId);
            entity.HasIndex(c => c.PaymentHash);
            entity.HasIndex(c => new { c.StoreId, c.InvoiceId, c.PaymentMethodId });
            entity.HasIndex(c => c.OutgoingSwapId).IsUnique();
            entity.Property(c => c.StoreId).IsRequired();
            entity.Property(c => c.PaymentMethodId).HasMaxLength(50);
            entity.Property(c => c.OutgoingSwapId).HasMaxLength(64);
            entity.Property(c => c.IngressSwapId).HasMaxLength(64);
            entity.Property(c => c.PaymentHash).HasMaxLength(64);
            entity.Property(c => c.CustomerDestination).HasMaxLength(8192);
        });
    }
}
