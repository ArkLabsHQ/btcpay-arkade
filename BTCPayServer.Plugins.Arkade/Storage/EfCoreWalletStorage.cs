using BTCPayServer.Plugins.Arkade.Data;
using BTCPayServer.Plugins.Arkade.Data.Entities;
using BTCPayServer.Plugins.Arkade.Wallet;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Intents;
using NArk.Swaps.Models;
using PluginArkWallet = BTCPayServer.Plugins.Arkade.Data.Entities.ArkWallet;

namespace BTCPayServer.Plugins.Arkade.Storage;

/// <summary>
/// Plugin-specific wallet storage for managing Ark wallet entities.
/// Handles wallet CRUD operations, destination settings, and HD wallet index tracking.
/// </summary>
public class EfCoreWalletStorage
{
    private readonly IDbContextFactory<ArkPluginDbContext> _dbContextFactory;

    /// <summary>
    /// Fired when a wallet is saved (created or updated).
    /// </summary>
    public event EventHandler<ArkWallet>? WalletSaved;

    /// <summary>
    /// Fired when a wallet is deleted.
    /// </summary>
    public event EventHandler<string>? WalletDeleted;

    public EfCoreWalletStorage(IDbContextFactory<ArkPluginDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<IReadOnlySet<ArkWallet>> LoadAllWallets(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var entities = await db.Wallets.ToListAsync(cancellationToken);
        return entities.ToHashSet();
    }

    public async Task<ArkWallet> LoadWallet(string walletIdentifierOrFingerprint, CancellationToken cancellationToken = default)
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

        return entity;
    }
    
    public async Task SaveWallet(string walletId, ArkWallet entity, string? walletDescriptor = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await db.Wallets.FirstOrDefaultAsync(w => w.Id == walletId, cancellationToken);

        if (existing != null)
        {
            // Update existing wallet
            existing.Wallet = entity.Wallet;
            existing.LastUsedIndex = entity.LastUsedIndex;
            // Store fingerprint in AccountDescriptor for HD wallets
            if (!string.IsNullOrEmpty(walletDescriptor))
            {
                // If HD wallet, store full descriptor; if just fingerprint, store that
                existing.AccountDescriptor ??= walletDescriptor;
            }
        }
        else
        {
            db.Wallets.Add(entity);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string GetFingerprint(ArkWallet entity)
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

    #region Plugin-specific methods

    /// <summary>
    /// Gets all wallets with their contracts and swaps included. Used for admin listing.
    /// </summary>
    public async Task<IReadOnlyList<ArkWallet>> GetWalletsWithDetailsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Wallets
            .Include(w => w.Contracts)
            .Include(w => w.Swaps)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets a wallet with its contracts and swaps included.
    /// </summary>
    public async Task<ArkWallet?> GetWalletWithDetailsAsync(
        string walletId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Wallets
            .Include(w => w.Contracts)
            .Include(w => w.Swaps)
            .FirstOrDefaultAsync(w => w.Id == walletId, cancellationToken);
    }

    /// <summary>
    /// Checks if a wallet has pending swaps.
    /// </summary>
    public async Task<bool> HasPendingSwapsAsync(
        string walletId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Swaps
            .AnyAsync(s => s.WalletId == walletId && s.Status == ArkSwapStatus.Pending, cancellationToken);
    }

    /// <summary>
    /// Checks if a wallet has pending intents.
    /// </summary>
    public async Task<bool> HasPendingIntentsAsync(
        string walletId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Intents
            .AnyAsync(i => i.WalletId == walletId &&
                          (i.State == ArkIntentState.WaitingToSubmit ||
                           i.State == ArkIntentState.WaitingForBatch), cancellationToken);
    }

    /// <summary>
    /// Deletes a wallet and all its associated data.
    /// </summary>
    public async Task<bool> DeleteWalletAsync(
        string walletId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var wallet = await db.Wallets
            .Include(w => w.Contracts)
            .FirstOrDefaultAsync(w => w.Id == walletId, cancellationToken);

        if (wallet == null)
            return false;

        // Get contract scripts for VTXO cleanup
        var contractScripts = wallet.Contracts.Select(c => c.Script).ToList();

        // Delete all VTXOs associated with the wallet's contracts
        var vtxos = await db.Vtxos
            .Where(v => contractScripts.Contains(v.Script))
            .ToListAsync(cancellationToken);
        db.Vtxos.RemoveRange(vtxos);

        // Delete all intents and their associated data
        var intents = await db.Intents
            .Include(i => i.IntentVtxos)
            .Where(i => i.WalletId == walletId)
            .ToListAsync(cancellationToken);
        db.Intents.RemoveRange(intents);

        // Delete the wallet (cascade will delete contracts and swaps)
        db.Wallets.Remove(wallet);

        await db.SaveChangesAsync(cancellationToken);

        WalletDeleted?.Invoke(this, walletId);

        return true;
    }

    /// <summary>
    /// Gets multiple wallets by their IDs.
    /// </summary>
    public async Task<IReadOnlyList<ArkWallet>> GetWalletsByIdsAsync(
        IEnumerable<string> walletIds,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var idSet = walletIds.ToHashSet();
        return await db.Wallets
            .Where(w => idSet.Contains(w.Id))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets a wallet by ID.
    /// </summary>
    public async Task<ArkWallet?> GetWalletByIdAsync(
        string walletId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Wallets.FindAsync([walletId], cancellationToken);
    }

    /// <summary>
    /// Updates wallet destination.
    /// </summary>
    public async Task UpdateWalletDestinationAsync(
        string walletId,
        string? destination,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var wallet = await db.Wallets.FindAsync([walletId], cancellationToken);
        if (wallet == null)
            throw new InvalidOperationException($"Wallet {walletId} not found.");

        wallet.WalletDestination = destination;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Gets all wallets with their secrets for signer loading.
    /// Returns wallet ID, secret (nsec or mnemonic), and wallet type.
    /// </summary>
    public async Task<IReadOnlyList<ArkWallet>> GetSignableWalletsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Wallets
            .Where(w => !string.IsNullOrEmpty(w.Wallet))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Upserts a wallet. Returns true if inserted, false if updated.
    /// Fires WalletSaved event on success.
    /// </summary>
    public async Task<bool> UpsertWalletAsync(
        ArkWallet wallet,
        bool updateIfExists = true,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await db.Wallets.FindAsync([wallet.Id], cancellationToken);
        bool inserted;

        if (existing == null)
        {
            db.Wallets.Add(wallet);
            inserted = true;
        }
        else if (updateIfExists)
        {
            db.Entry(existing).CurrentValues.SetValues(wallet);
            inserted = false;
        }
        else
        {
            return false;
        }

        await db.SaveChangesAsync(cancellationToken);
        WalletSaved?.Invoke(this, wallet);

        return inserted;
    }

    /// <summary>
    /// Updates wallet last used index.
    /// </summary>
    public async Task UpdateWalletLastUsedIndexAsync(
        string walletId,
        int lastUsedIndex,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var wallet = await db.Wallets.FindAsync([walletId], cancellationToken);
        if (wallet != null)
        {
            wallet.LastUsedIndex = lastUsedIndex;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    #endregion
}
