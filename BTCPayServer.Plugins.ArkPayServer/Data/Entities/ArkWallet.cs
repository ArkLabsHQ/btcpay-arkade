using Microsoft.EntityFrameworkCore;
using NArk;

namespace BTCPayServer.Plugins.ArkPayServer.Data.Entities;

public class ArkWallet
{
    public string Id { get; set; }
    public string Wallet { get; set; }
    public string? WalletDestination { get; set; }
    public List<ArkWalletContract> Contracts { get; set; } = [];

    /// <summary>
    /// JSON-serialized intent scheduling policies for this wallet
    /// </summary>
    public string? IntentSchedulingPolicy { get; set; }

    /// <summary>
    /// Type of wallet: Nsec (legacy single-key) or Mnemonic (HD wallet)
    /// </summary>
    public WalletType WalletType { get; set; } = WalletType.Nsec;

    /// <summary>
    /// Last used derivation index for HD wallets. Null for nsec wallets.
    /// </summary>
    public int? LastUsedIndex { get; set; }

    /// <summary>
    /// Account descriptor for HD wallets in format: tr([fingerprint/86'/coin'/0']xpub/0/*)
    /// For nsec tr(PUBLIC_KEY_HEX)
    /// </summary>
    public string AccountDescriptor { get; set; }
    
    public ArkAddress? Destination => string.IsNullOrEmpty(WalletDestination)? null: ArkAddress.Parse(WalletDestination);

    public List<ArkSwap> Swaps { get; set; }

    internal static void OnModelCreating(ModelBuilder builder)
    {
        var entity = builder.Entity<ArkWallet>();
        entity.HasKey(w => w.Id);
        entity.HasIndex(w => w.Wallet).IsUnique();
        entity.Property(w => w.WalletType).HasDefaultValue(WalletType.Nsec);
        entity.HasMany(w => w.Contracts)
            .WithOne(contract => contract.Wallet)
            .HasForeignKey(contract => contract.WalletId);
        entity.HasMany(w => w.Swaps)
            .WithOne(contract => contract.Wallet)
            .HasForeignKey(contract => contract.WalletId);
    }
}
