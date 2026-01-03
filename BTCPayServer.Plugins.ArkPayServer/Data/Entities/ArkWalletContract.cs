using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ArkPayServer.Data.Entities;

public class ArkWalletContract
{
    [Key]
    public string Script { get; set; }

    public bool Active { get; set; }
    public string Type { get; set; }

    [Column(TypeName = "jsonb")]
    public Dictionary<string, string> ContractData { get; set; }

    public ArkWallet Wallet { get; set; }
    public string WalletId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The output descriptor of the signing entity used to create this contract.
    /// For HD wallets: full descriptor with derivation path (e.g., tr([fp/86'/0'/0']xpub/0/5))
    /// For legacy wallets: simple tr(pubkey) descriptor
    /// Used to look up the correct key for signing.
    /// </summary>
    public string? SigningEntityDescriptor { get; set; }

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

        entity.HasOne(w => w.Wallet)
            .WithMany(w => w.Contracts)
            .HasForeignKey(w => w.WalletId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
