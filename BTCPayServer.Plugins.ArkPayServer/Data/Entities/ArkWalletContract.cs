using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using BTCPayServer.Plugins.ArkPayServer.Data;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Contracts;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ArkPayServer.Data.Entities;

public class ArkWalletContract
{
    [Key]
    public string Script { get; set; }

    public ContractActivityState ActivityState { get; set; } = ContractActivityState.Inactive;
    public string Type { get; set; }

    [Column(TypeName = "jsonb")]
    public Dictionary<string, string> ContractData { get; set; }

    [Column(TypeName = "jsonb")]
    public Dictionary<string, string>? Metadata { get; set; }

    public ArkWallet Wallet { get; set; }
    public string WalletId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ArkSwap> Swaps { get; set; }

    internal static void OnModelCreating(ModelBuilder builder)
    {
        var entity = builder.Entity<ArkWalletContract>();
        entity.HasKey(w => new { w.Script, w.WalletId });

        // FIXME!
        // I could not get the storing of Json to work, so storing contract data as a string for now,
        // But still in a jsonb field...
        entity.Property(e => e.ContractData)
            .HasConversion(
                v => JsonConvert.SerializeObject(v),
                v => JsonConvert.DeserializeObject<Dictionary<string, string>>(v) ?? new Dictionary<string, string>())
            .HasColumnType("jsonb");

        entity.Property(e => e.Metadata)
            .HasConversion(
                v => v == null ? null : JsonConvert.SerializeObject(v),
                v => v == null ? null : JsonConvert.DeserializeObject<Dictionary<string, string>>(v))
            .HasColumnType("jsonb");

        entity.HasOne(w => w.Wallet)
            .WithMany(w => w.Contracts)
            .HasForeignKey(w => w.WalletId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
