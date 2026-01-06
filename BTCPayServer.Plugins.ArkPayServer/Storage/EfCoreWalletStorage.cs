using System.Text;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Wallet;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Wallets;
using PluginArkWallet = BTCPayServer.Plugins.ArkPayServer.Data.Entities.ArkWallet;
using NNarkArkWallet = NArk.Abstractions.Wallets.ArkWallet;

namespace BTCPayServer.Plugins.ArkPayServer.Storage;

/// <summary>
/// EF Core implementation of NNark's IWalletStorage interface.
/// Maps between plugin's ArkWallet entity and NNark's ArkWallet record.
/// </summary>
public class EfCoreWalletStorage : IWalletStorage
{
    private readonly IDbContextFactory<ArkPluginDbContext> _dbContextFactory;

    public EfCoreWalletStorage(IDbContextFactory<ArkPluginDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<IReadOnlySet<NNarkArkWallet>> LoadAllWallets(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var entities = await db.Wallets.ToListAsync(cancellationToken);
        return entities.Select(MapToNNarkWallet).ToHashSet();
    }

    public async Task<NNarkArkWallet> LoadWallet(string walletIdentifierOrFingerprint, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // First try to find by ID
        var entity = await db.Wallets.FirstOrDefaultAsync(
            w => w.Id == walletIdentifierOrFingerprint, cancellationToken);

        // If not found, try by fingerprint (for HD wallets, fingerprint is stored in AccountDescriptor)
        if (entity == null)
        {
            entity = await db.Wallets.FirstOrDefaultAsync(
                w => w.AccountDescriptor != null && w.AccountDescriptor.Contains(walletIdentifierOrFingerprint),
                cancellationToken);
        }

        if (entity == null)
        {
            throw new KeyNotFoundException($"Wallet {walletIdentifierOrFingerprint} not found");
        }

        return MapToNNarkWallet(entity);
    }

    public async Task SaveWallet(string walletId, NNarkArkWallet arkWallet, string? walletFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await db.Wallets.FirstOrDefaultAsync(w => w.Id == walletId, cancellationToken);

        if (existing != null)
        {
            // Update existing wallet
            existing.Wallet = Encoding.UTF8.GetString(arkWallet.WalletPrivateBytes);
            existing.LastUsedIndex = arkWallet.LastAddressIndex;
            // Store fingerprint in AccountDescriptor for HD wallets
            if (!string.IsNullOrEmpty(walletFingerprint))
            {
                // If HD wallet, store full descriptor; if just fingerprint, store that
                existing.AccountDescriptor ??= walletFingerprint;
            }
        }
        else
        {
            // Create new wallet
            var entity = new PluginArkWallet
            {
                Id = walletId,
                Wallet = Encoding.UTF8.GetString(arkWallet.WalletPrivateBytes),
                WalletType = arkWallet.GetWalletType(),
                LastUsedIndex = arkWallet.LastAddressIndex,
                AccountDescriptor = walletFingerprint
            };
            db.Wallets.Add(entity);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static NNarkArkWallet MapToNNarkWallet(PluginArkWallet entity)
    {
        var fingerprint = GetFingerprint(entity);
        return new NNarkArkWallet(
            WalletIdentifier: entity.Id,
            WalletFingerprint: fingerprint,
            WalletPrivateBytes: Encoding.UTF8.GetBytes(entity.Wallet),
            LastAddressIndex: entity.LastUsedIndex
        );
    }

    private static string GetFingerprint(PluginArkWallet entity)
    {
        // For HD wallets, extract fingerprint from AccountDescriptor
        // Format: tr([fingerprint/86'/coin'/0']xpub...)
        if (entity.WalletType == WalletType.HD && !string.IsNullOrEmpty(entity.AccountDescriptor))
        {
            var start = entity.AccountDescriptor.IndexOf('[');
            var slash = entity.AccountDescriptor.IndexOf('/');
            if (start >= 0 && slash > start)
            {
                return entity.AccountDescriptor.Substring(start + 1, slash - start - 1);
            }
        }

        // For legacy wallets, use the wallet ID (which is the pubkey hex)
        return entity.Id;
    }
}
