using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using BTCPayServer.Plugins.Arkade.Data;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Contracts;

namespace BTCPayServer.Plugins.Arkade.Data.Entities;

public class ArkWalletContract
{
    [Key]
    public string Script { get; set; }

    public ContractActivityState ActivityState { get; set; } = ContractActivityState.Inactive;
    public string Type { get; set; }

    [Column("ContractData", TypeName = "jsonb")]
    public string ContractDataJson { get; set; } = "{}";

    [Column("Metadata", TypeName = "jsonb")]
    public string? MetadataJson { get; set; }

    [NotMapped]
    public Dictionary<string, string> ContractData
    {
        get => JsonSerializer.Deserialize<Dictionary<string, string>>(ContractDataJson) ?? new();
        set => ContractDataJson = JsonSerializer.Serialize(value);
    }

    [NotMapped]
    public Dictionary<string, string>? Metadata
    {
        get => MetadataJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(MetadataJson);
        set => MetadataJson = value is null ? null : JsonSerializer.Serialize(value);
    }

    public ArkWallet Wallet { get; set; }
    public string WalletId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ArkSwap> Swaps { get; set; }

    internal static void OnModelCreating(ModelBuilder builder)
    {
        var entity = builder.Entity<ArkWalletContract>();
        entity.HasKey(w => new { w.Script, w.WalletId });

        entity.HasOne(w => w.Wallet)
            .WithMany(w => w.Contracts)
            .HasForeignKey(w => w.WalletId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
