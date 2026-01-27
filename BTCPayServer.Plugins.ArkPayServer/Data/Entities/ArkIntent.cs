using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Intents;

namespace BTCPayServer.Plugins.ArkPayServer.Data;

public class ArkIntent
{
    /// <summary>
    /// The unique transaction ID for this intent (primary key).
    /// </summary>
    public string IntentTxId { get; set; } = "";

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
        entity.HasKey(e => e.IntentTxId);
        entity.HasIndex(e => e.IntentId).IsUnique().HasFilter("\"IntentId\" IS NOT NULL");
        entity.Property(e => e.BatchId).HasDefaultValue(null);
        entity.Property(e => e.CommitmentTransactionId).HasDefaultValue(null);
        entity.Property(e => e.CancellationReason).HasDefaultValue(null);
        entity.Property(e => e.SignerDescriptor).HasDefaultValue(null);
        entity.HasMany(e => e.IntentVtxos)
            .WithOne(e => e.Intent)
            .HasForeignKey(e => e.IntentTxId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
