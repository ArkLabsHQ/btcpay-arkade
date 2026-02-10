using BTCPayServer.Plugins.Arkade.Wallet;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Arkade.Data.Entities;

public class ArkWallet
{
    public string Id { get; set; }

    /// <summary>
    /// For legacy wallets: the nsec private key.
    /// For HD wallets: the BIP-39 mnemonic.
    /// </summary>
    public string Wallet { get; set; }

    /// <summary>
    /// Destination address for swept funds.
    /// </summary>
    public string? WalletDestination { get; set; }

    public List<ArkWalletContract> Contracts { get; set; } = [];
    
    /// <summary>
    /// The type of wallet (Legacy nsec or HD mnemonic).
    /// Defaults to Legacy for backwards compatibility.
    /// </summary>
    public WalletType WalletType { get; set; } = WalletType.SingleKey;

    /// <summary>
    /// For HD wallets: the account descriptor (e.g., tr([fingerprint/86'/0'/0']xpub...)).
    /// For legacy wallets: the simple tr(pubkey) descriptor.
    /// </summary>
    public string? AccountDescriptor { get; set; }

    /// <summary>
    /// For HD wallets: the last used derivation index.
    /// Incremented each time a new signing entity is created.
    /// </summary>
    public int LastUsedIndex { get; set; } = 0;

    public List<ArkSwap> Swaps { get; set; }

    internal static void OnModelCreating(ModelBuilder builder)
    {
        var entity = builder.Entity<ArkWallet>();
        entity.HasKey(w => w.Id);
        entity.HasIndex(w => w.Wallet).IsUnique();
        entity.Property(w => w.WalletType).HasDefaultValue(WalletType.SingleKey);
        entity.Property(w => w.AccountDescriptor).HasDefaultValue("TODO_MIGRATION");
        entity.Property(w => w.LastUsedIndex).HasDefaultValue(0);
        entity.HasMany(w => w.Contracts)
            .WithOne(contract => contract.Wallet)
            .HasForeignKey(contract => contract.WalletId);
        entity.HasMany(w => w.Swaps)
            .WithOne(contract => contract.Wallet)
            .HasForeignKey(contract => contract.WalletId);
    }
}
