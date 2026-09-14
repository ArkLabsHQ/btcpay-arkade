using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NArk.Storage.EfCore.Entities;

namespace BTCPayServer.Plugins.ArkPayServer.Data.Legacy;

public enum LegacySwapStatus { Pending = 0, Settled = 1, Failed = 2, Refunded = 3, Unknown = 4 }
public enum LegacySwapType { ReverseSubmarine = 0, Submarine = 1, ChainBtcToArk = 2, ChainArkToBtc = 3 }

// These values and mappings retain the historical Swaps table without a swap execution service.
public sealed class LegacySwap
{
    public string SwapId { get; set; } = "";
    public string WalletId { get; set; } = "";
    public LegacySwapType SwapType { get; set; }
    public string Invoice { get; set; } = "";
    public long ExpectedAmount { get; set; }
    public string ContractScript { get; set; } = "";
    public string? Address { get; set; }
    public LegacySwapStatus Status { get; set; }
    public string? FailReason { get; set; }
    [Column("Metadata", TypeName = "jsonb")]
    public string? MetadataJson { get; set; }
    [NotMapped]
    public Dictionary<string, string>? Metadata
    {
        get => MetadataJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(MetadataJson);
        set => MetadataJson = value is null ? null : JsonSerializer.Serialize(value);
    }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string Hash { get; set; } = "";
    public ArkWalletContractEntity Contract { get; set; } = null!;
    public ArkWalletEntity Wallet { get; set; } = null!;

    internal static void Configure(EntityTypeBuilder<LegacySwap> builder)
    {
        builder.ToTable("Swaps", "BTCPayServer.Plugins.Ark");
        builder.HasKey(e => new { e.SwapId, e.WalletId });
        builder.Property(e => e.FailReason).HasDefaultValue(null);
        builder.Property(e => e.Address).HasDefaultValue(null);
        builder.Property(e => e.MetadataJson).HasDefaultValue(null);
        builder.HasOne(e => e.Contract).WithMany()
            .HasForeignKey(e => new { e.ContractScript, e.WalletId });
        builder.HasOne(e => e.Wallet).WithMany().HasForeignKey(e => e.WalletId);
    }
}
