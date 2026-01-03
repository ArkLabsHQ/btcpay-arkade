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
        ArkSwap.OnModelCreating(modelBuilder);
        ArkIntent.OnModelCreating(modelBuilder);
        ArkIntentVtxo.OnModelCreating(modelBuilder);
        // BoardingAddress.OnModelCreating(modelBuilder);
    }
}


public class ArkIntent
{
    public int InternalId { get; set; }
    public string? IntentId { get; set; }
    public string WalletId { get; set; }
    public ArkIntentState State { get; set; }

    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset ValidUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<ArkIntentVtxo> IntentVtxos { get; set; }

    public string RegisterProof { get; set; }
    public string RegisterProofMessage { get; set; }
    public string DeleteProof { get; set; }
    public string DeleteProofMessage { get; set; }

    public string? BatchId { get; set; }
    public string? CommitmentTransactionId { get; set; }
    public string? CancellationReason { get; set; }

    public string[] PartialForfeits { get; set; } = [];

    /// <summary>
    /// The output descriptor of the signing entity used for this intent.
    /// Required for HD wallets to look up the correct key for signing.
    /// </summary>
    public string? SignerDescriptor { get; set; }

    internal static void OnModelCreating(ModelBuilder builder)
    {
        var entity = builder.Entity<ArkIntent>();
        entity.HasKey(e => e.InternalId);
        entity.Property(e => e.InternalId).ValueGeneratedOnAdd();
        entity.HasIndex(e => e.IntentId).IsUnique().HasFilter("\"IntentId\" IS NOT NULL");
        entity.Property(e => e.BatchId).HasDefaultValue(null);
        entity.Property(e => e.CommitmentTransactionId).HasDefaultValue(null);
        entity.Property(e => e.CancellationReason).HasDefaultValue(null);
        entity.Property(e => e.SignerDescriptor).HasDefaultValue(null);
        entity.HasMany(e => e.IntentVtxos)
            .WithOne(e => e.Intent)
            .HasForeignKey(e => e.InternalId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public enum ArkIntentState
{
    WaitingToSubmit,
    WaitingForBatch,
    BatchInProgress,
    BatchFailed,
    BatchSucceeded,
    Cancelled
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