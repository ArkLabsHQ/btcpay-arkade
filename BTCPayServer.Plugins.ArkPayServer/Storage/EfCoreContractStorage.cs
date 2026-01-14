using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NArk.Abstractions.Contracts;

namespace BTCPayServer.Plugins.ArkPayServer.Storage;

/// <summary>
/// EF Core implementation of NNark's IContractStorage interface.
/// Maps between plugin's ArkWalletContract entity and NNark's ArkContractEntity record.
/// </summary>
public class EfCoreContractStorage : IContractStorage
{
    private readonly IDbContextFactory<ArkPluginDbContext> _dbContextFactory;

    public event EventHandler<ArkContractEntity>? ContractsChanged;
    public event EventHandler? ActiveScriptsChanged;

    public EfCoreContractStorage(IDbContextFactory<ArkPluginDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<IReadOnlySet<ArkContractEntity>> LoadAllContractsByWallet(
        string walletIdentifier,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var entities = await db.WalletContracts
            .Where(c => c.WalletId == walletIdentifier)
            .ToListAsync(cancellationToken);

        return entities.Select(MapToArkContractEntity).ToHashSet();
    }

    public async Task<IReadOnlySet<ArkContractEntity>> LoadActiveContracts(
        IReadOnlyCollection<string>? walletIdentifiers = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.WalletContracts.Where(c => c.ActivityState != ContractActivityState.Inactive);

        if (walletIdentifiers != null && walletIdentifiers.Count > 0)
        {
            var walletSet = walletIdentifiers.ToHashSet();
            query = query.Where(c => walletSet.Contains(c.WalletId));
        }

        var entities = await query.ToListAsync(cancellationToken);
        return entities.Select(MapToArkContractEntity).ToHashSet();
    }

    public async Task<IReadOnlySet<ArkContractEntity>> LoadContractsByScripts(string[] scripts, IReadOnlyCollection<string>? walletIdentifiers = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var scriptSet = scripts.ToHashSet();
        var entities = await db.WalletContracts
            .Where(c => scriptSet.Contains(c.Script))
            .Where(contract => walletIdentifiers == null || walletIdentifiers.Contains(contract.Script))
            .ToListAsync(cancellationToken);

        return entities.Select(MapToArkContractEntity).ToHashSet();
    }

    public async Task SaveContract(
        ArkContractEntity walletEntity,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await db.WalletContracts.FirstOrDefaultAsync(
            c => c.Script == walletEntity.Script && c.WalletId == walletEntity.WalletIdentifier,
            cancellationToken);

        if (existing != null)
        {
            existing.ActivityState = walletEntity.ActivityState;
            existing.Type = walletEntity.Type;
            existing.ContractData = walletEntity.AdditionalData;
        }
        else
        {
            var entity = new ArkWalletContract
            {
                Script = walletEntity.Script,
                WalletId = walletEntity.WalletIdentifier,
                ActivityState = walletEntity.ActivityState,
                Type = walletEntity.Type,
                ContractData = walletEntity.AdditionalData,
                CreatedAt = walletEntity.CreatedAt
            };
            db.WalletContracts.Add(entity);
        }

        if (await db.SaveChangesAsync(cancellationToken) > 0)
        {
            ContractsChanged?.Invoke(this, walletEntity);
            ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
        }

    }

    private static ArkContractEntity MapToArkContractEntity(ArkWalletContract entity)
    {
        return new ArkContractEntity(
            Script: entity.Script,
            ActivityState: entity.ActivityState,
            Type: entity.Type,
            AdditionalData: entity.ContractData,
            WalletIdentifier: entity.WalletId,
            CreatedAt: entity.CreatedAt
        );
    }

    #region Plugin-specific methods (wallet-guarded)

    /// <summary>
    /// Gets the first active contract for a wallet. Used for displaying default address.
    /// </summary>
    public async Task<ArkWalletContract?> GetFirstActiveContractAsync(
        string walletId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.WalletContracts
            .Where(c => c.WalletId == walletId && c.ActivityState != ContractActivityState.Inactive)
            .OrderBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Gets a contract with its swaps included. Wallet ID guards ownership.
    /// </summary>
    public async Task<ArkWalletContract?> GetContractWithSwapsAsync(
        string walletId,
        string script,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.WalletContracts
            .Include(c => c.Swaps)
            .FirstOrDefaultAsync(c => c.WalletId == walletId && c.Script == script, cancellationToken);
    }

    /// <summary>
    /// Checks if a contract with the given script exists for the wallet.
    /// </summary>
    public async Task<bool> ContractExistsAsync(
        string walletId,
        string script,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.WalletContracts
            .AnyAsync(c => c.WalletId == walletId && c.Script == script, cancellationToken);
    }

    /// <summary>
    /// Deletes a contract by script. Wallet ID guards ownership.
    /// </summary>
    public async Task<bool> DeleteContractAsync(
        string walletId,
        string script,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var contract = await db.WalletContracts
            .FirstOrDefaultAsync(c => c.WalletId == walletId && c.Script == script, cancellationToken);

        if (contract == null)
            return false;

        db.WalletContracts.Remove(contract);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Gets all contract scripts for a wallet.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetContractScriptsAsync(
        string walletId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.WalletContracts
            .Where(c => c.WalletId == walletId)
            .Select(c => c.Script)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets contract scripts for active contracts only.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetActiveContractScriptsAsync(
        string walletId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.WalletContracts
            .Where(c => c.WalletId == walletId && c.ActivityState != ContractActivityState.Inactive)
            .Select(c => c.Script)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Sets contract activity state.
    /// </summary>
    public async Task<bool> SetContractActivityStateAsync(
        string walletId,
        string script,
        ContractActivityState activityState,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var contract = await db.WalletContracts.FirstOrDefaultAsync(
            c => c.WalletId == walletId && c.Script == script && c.ActivityState != activityState,
            cancellationToken);

        if (contract == null)
            return false;

        contract.ActivityState = activityState;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Upserts a contract.
    /// </summary>
    public async Task<bool> UpsertContractAsync(
        ArkWalletContract contract,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await db.WalletContracts.FirstOrDefaultAsync(
            c => c.Script == contract.Script && c.WalletId == contract.WalletId,
            cancellationToken);

        if (existing != null)
        {
            existing.ActivityState = contract.ActivityState;
            existing.ContractData = contract.ContractData;
        }
        else
        {
            await db.WalletContracts.AddAsync(contract, cancellationToken);
        }

        return await db.SaveChangesAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// Gets contracts with pagination and optional filtering.
    /// </summary>
    /// <param name="walletId">The wallet to get contracts for</param>
    /// <param name="skip">Number of contracts to skip</param>
    /// <param name="count">Number of contracts to return</param>
    /// <param name="searchText">Optional search text to filter by script</param>
    /// <param name="filterIsActive">If true, returns only active contracts (not Inactive); if false, returns only Inactive contracts; if null, returns all</param>
    /// <param name="includeSwaps">Whether to include swaps in the result</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<IReadOnlyList<ArkWalletContract>> GetContractsWithPaginationAsync(
        string walletId,
        int skip = 0,
        int count = 10,
        string? searchText = null,
        bool? filterIsActive = null,
        bool includeSwaps = false,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = includeSwaps
            ? db.WalletContracts.Include(c => c.Swaps)
            : db.WalletContracts.AsQueryable();

        query = query.Where(c => c.WalletId == walletId);

        if (!string.IsNullOrEmpty(searchText))
        {
            query = query.Where(c => c.Script.Contains(searchText));
        }

        if (filterIsActive.HasValue)
        {
            // Active = not Inactive (includes Active and AwaitingFundsBeforeDeactivate)
            query = filterIsActive.Value
                ? query.Where(c => c.ActivityState != ContractActivityState.Inactive)
                : query.Where(c => c.ActivityState == ContractActivityState.Inactive);
        }

        return await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip(skip)
            .Take(count)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets contracts for multiple wallets with optional filtering.
    /// </summary>
    /// <param name="walletIds">Optional list of wallet IDs to filter by</param>
    /// <param name="searchText">Optional search text to filter by script</param>
    /// <param name="filterIsActive">If true, returns only active contracts; if false, returns only Inactive contracts; if null, returns all</param>
    /// <param name="includeSwaps">Whether to include swaps in the result</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<IReadOnlyList<ArkWalletContract>> GetContractsForWalletsAsync(
        string[]? walletIds = null,
        string? searchText = null,
        bool? filterIsActive = null,
        bool includeSwaps = false,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = includeSwaps
            ? db.WalletContracts.Include(c => c.Swaps)
            : db.WalletContracts.AsQueryable();

        if (walletIds != null && walletIds.Length > 0)
        {
            query = query.Where(c => walletIds.Contains(c.WalletId));
        }

        if (!string.IsNullOrEmpty(searchText))
        {
            query = query.Where(c => c.Script.Contains(searchText));
        }

        if (filterIsActive.HasValue)
        {
            query = filterIsActive.Value
                ? query.Where(c => c.ActivityState != ContractActivityState.Inactive)
                : query.Where(c => c.ActivityState == ContractActivityState.Inactive);
        }

        return await query
            .OrderByDescending(c => c.CreatedAt)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Deactivates all contracts with the given script that are in AwaitingFundsBeforeDeactivate state.
    /// Called when a VTXO is received to auto-deactivate one-time-use contracts (like refund addresses).
    /// </summary>
    public async Task<int> DeactivateAwaitingContractsByScript(
        string script,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var contracts = await db.WalletContracts
            .Where(c => c.Script == script && c.ActivityState == ContractActivityState.AwaitingFundsBeforeDeactivate)
            .ToListAsync(cancellationToken);

        if (contracts.Count == 0)
            return 0;

        foreach (var contract in contracts)
        {
            contract.ActivityState = ContractActivityState.Inactive;
        }

        var count = await db.SaveChangesAsync(cancellationToken);

        // Raise events for each deactivated contract
        foreach (var contract in contracts)
        {
            ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
        }

        return count;
    }

    #endregion

}
