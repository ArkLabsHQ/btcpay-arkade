using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using BTCPayServer.Client.Models;
using Microsoft.EntityFrameworkCore;
using NArk;
using NArk.Extensions;
using NArk.Services;
using NArk.Models;
using NBitcoin.Secp256k1;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities.Enums;

namespace BTCPayServer.Plugins.ArkPayServer.Data.Entities;

public class ArkWallet
{
    public string Id { get; set; }
    public string Wallet { get; set; }
    [DefaultValue(WalletType.PrivateKey)]
    public WalletType WalletType { get; set; } = WalletType.PrivateKey;
    public int LatestIndexUsed { get; set; } = -1;
    public string? WalletDestination { get; set; }
    public List<ArkWalletContract> Contracts { get; set; } = [];
    
    /// <summary>
    /// JSON-serialized intent scheduling policies for this wallet
    /// </summary>
    public string? IntentSchedulingPolicy { get; set; }

    public ECXOnlyPubKey RequestNewPublicKey(int? index = null) => WalletType switch
    {
        WalletType.Seed => KeyExtensions.GetXOnlyPubKeyFromWallet(Wallet, index ?? LatestIndexUsed + 1),
        WalletType.PrivateKey => KeyExtensions.GetXOnlyPubKeyFromWallet(Wallet),
        _ => throw new ArgumentOutOfRangeException()
    };
    
    public ArkAddress? Destination => string.IsNullOrEmpty(WalletDestination)? null: ArkAddress.Parse(WalletDestination);

    public List<ArkSwap> Swaps { get; set; }

    internal static void OnModelCreating(ModelBuilder builder)
    {
        var entity = builder.Entity<ArkWallet>();
        entity.HasKey(w => w.Id);
        entity.HasIndex(w => w.Wallet).IsUnique();
        entity.Property(e => e.WalletType)
            .HasConversion<string>();
        entity.HasMany(w => w.Contracts)
            .WithOne(contract => contract.Wallet)
            .HasForeignKey(contract => contract.WalletId);
        entity.HasMany(w => w.Swaps)
            .WithOne(contract => contract.Wallet)
            .HasForeignKey(contract => contract.WalletId);
    }
}
