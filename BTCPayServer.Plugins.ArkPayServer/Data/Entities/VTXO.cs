using Microsoft.EntityFrameworkCore;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Data.Entities;

public class VTXO
{
    public string Script { get; set; }
    public string TransactionId { get; set; }
    public int TransactionOutputIndex { get; set; }
    public string? SpentByTransactionId { get; set; }
    // public int? SpentByTransactionIdInputIndex { get; set; }
    public long Amount { get; set; }
    public DateTimeOffset SeenAt { get; set; }
    // public DateTimeOffset? SpentAt { get; set; }
    public bool Recoverable { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public uint? ExpiresAtHeight { get; set; }

    public virtual ICollection<ArkIntentVtxo> IntentVtxos { get; set; } = null!;

    public bool IsExpired((long Timestamp, uint Height) now)
    {
        if (ExpiresAt is not null && DateTimeOffset.FromUnixTimeSeconds(now.Timestamp) >= ExpiresAt)
            return true;
        if (ExpiresAtHeight is not null && now.Height >= ExpiresAtHeight)
            return true;
        return false;
    }

    public ICoinable ToCoin()
    { 
        var outpoint = new OutPoint(new uint256(TransactionId), (uint) TransactionOutputIndex);
        var txout = new TxOut(Money.Satoshis(Amount), NBitcoin.Script.FromHex(Script));
        return new Coin(outpoint, txout);
    }

    public override int GetHashCode()
    {
        // Hash all properties to detect changes
        return HashCode.Combine(
            TransactionId,
            TransactionOutputIndex,
            Amount,
            Recoverable,
            SeenAt,
            ExpiresAt?.ToString() ?? ExpiresAtHeight?.ToString(),
            SpentByTransactionId,
            Script
        );
    }
    
    internal static void OnModelCreating(ModelBuilder builder)
    {
        var entity = builder.Entity<VTXO>();
        entity.Property(vtxo => vtxo.SpentByTransactionId).HasDefaultValue(null);
        
        entity.HasKey(e => new { e.TransactionId, e.TransactionOutputIndex });
    }
}