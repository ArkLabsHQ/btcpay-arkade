using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.ArkPayServer.Data;

public class ArkPluginDbContext(DbContextOptions<ArkPluginDbContext> options) : DbContext(options)
{
    public DbSet<ArkWallet> Wallets { get; set; }
    public DbSet<ArkWalletContract> WalletContracts { get; set; }
    public DbSet<VTXO> Vtxos { get; set; }
    public DbSet<ArkSwap> Swaps { get; set; }
    public DbSet<ArkIntent> Intents { get; set; }
    public DbSet<ArkIntentVtxo> IntentVtxos { get; set; }
    // public DbSet<BoardingAddress> BoardingAddresses { get; set; }
    // public DbSet<ArkStoredTransaction> Transactions { get; set; }
    
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

public class ArkIntentVtxo
{
    public int InternalId { get; set; }
    public ArkIntent Intent { get; set; }
    
    public string VtxoTransactionId { get; set; }
    public int VtxoTransactionOutputIndex { get; set; }
    public VTXO Vtxo { get; set; }
    
    public DateTimeOffset LinkedAt { get; set; }
    
    internal static void OnModelCreating(ModelBuilder builder)
    {
        var entity = builder.Entity<ArkIntentVtxo>();
        entity.HasKey(e => new { e.InternalId, e.VtxoTransactionId, e.VtxoTransactionOutputIndex });
        
        entity.HasOne(e => e.Vtxo)
            .WithMany(v => v.IntentVtxos)
            .HasForeignKey(e => new { e.VtxoTransactionId, e.VtxoTransactionOutputIndex })
            .OnDelete(DeleteBehavior.Cascade);
        
        // Index for querying VTXOs by their outpoint (useful for checking if VTXO is locked)
        entity.HasIndex(e => new { e.VtxoTransactionId, e.VtxoTransactionOutputIndex });
    }
}