using BTCPayServer.Plugins.Arkade.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Arkade.Data;

public class ArkPluginDbContext(DbContextOptions<ArkPluginDbContext> options) : DbContext(options)
{
    public DbSet<ArkWallet> Wallets { get; set; }
    public DbSet<ArkWalletContract> WalletContracts { get; set; }
    public DbSet<VTXO> Vtxos { get; set; }
    public DbSet<ArkSwap> Swaps { get; set; }
    public DbSet<ArkIntent> Intents { get; set; }
    public DbSet<ArkIntentVtxo> IntentVtxos { get; set; }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("BTCPayServer.Plugins.Ark");
        SetupDbRelations(modelBuilder);
    }

    private static void SetupDbRelations(ModelBuilder modelBuilder)
    {
        // ArkStoredTransaction.OnModelCreating(modelBuilder);
        VTXO.OnModelCreating(modelBuilder);
        ArkWallet.OnModelCreating(modelBuilder);
        ArkWalletContract.OnModelCreating(modelBuilder);
        ArkIntent.OnModelCreating(modelBuilder);
        ArkIntentVtxo.OnModelCreating(modelBuilder);
        ArkSwap.OnModelCreating(modelBuilder);
        // BoardingAddress.OnModelCreating(modelBuilder);
    }
}