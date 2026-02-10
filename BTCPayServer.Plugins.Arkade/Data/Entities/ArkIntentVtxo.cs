using BTCPayServer.Plugins.Arkade.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Arkade.Data;

public class ArkIntentVtxo
{
    public string IntentTxId { get; set; }
    public ArkIntent Intent { get; set; }

    public string VtxoTransactionId { get; set; }
    public int VtxoTransactionOutputIndex { get; set; }
    public VTXO Vtxo { get; set; }

    public DateTimeOffset LinkedAt { get; set; }

    internal static void OnModelCreating(ModelBuilder builder)
    {
        var entity = builder.Entity<ArkIntentVtxo>();
        entity.HasKey(e => new { e.IntentTxId, e.VtxoTransactionId, e.VtxoTransactionOutputIndex });

        entity.HasOne(e => e.Vtxo)
            .WithMany(v => v.IntentVtxos)
            .HasForeignKey(e => new { e.VtxoTransactionId, e.VtxoTransactionOutputIndex })
            .OnDelete(DeleteBehavior.Cascade);

        // Index for querying VTXOs by their outpoint (useful for checking if VTXO is locked)
        entity.HasIndex(e => new { e.VtxoTransactionId, e.VtxoTransactionOutputIndex });
    }
}
