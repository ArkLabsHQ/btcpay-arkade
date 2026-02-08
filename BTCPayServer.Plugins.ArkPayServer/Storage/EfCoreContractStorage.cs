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

    public async Task<IReadOnlyCollection<ArkContractEntity>> GetContracts(
        string[]? walletIds = null,
        string[]? scripts = null,
        bool? isActive = null,
        string[]? contractTypes = null,
        string? searchText = null,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        IQueryable<ArkWalletContract> query;

        // When searching, use raw SQL as the base to cast jsonb Metadata to text for ILIKE.
        // PostgreSQL has no LIKE/ILIKE operator for jsonb, so the cast is required.
        // All other filters compose on top via LINQ as usual.
        if (!string.IsNullOrEmpty(searchText))
        {
            var pattern = $"%{searchText}%";
            query = db.WalletContracts
                .FromSqlInterpolated($@"SELECT * FROM ""BTCPayServer.Plugins.Ark"".""WalletContracts"" WHERE ""Script"" ILIKE {pattern} OR ""Type"" ILIKE {pattern} OR ""Metadata""::text ILIKE {pattern}");
        }
        else
        {
            query = db.WalletContracts.AsQueryable();
        }

        // Filter by wallet IDs
        if (walletIds is {  })
        {
            query = query.Where(c => walletIds.Contains(c.WalletId));
        }

        // Filter by scripts
        if (scripts is {  })
        {
            var scriptSet = scripts.ToHashSet();
            query = query.Where(c => scriptSet.Contains(c.Script));
        }

        // Filter by activity state
        if (isActive.HasValue)
        {
            query = isActive.Value
                ? query.Where(c => c.ActivityState != ContractActivityState.Inactive)
                : query.Where(c => c.ActivityState == ContractActivityState.Inactive);
        }

        // Filter by contract types
        if (contractTypes is {  })
        {
            query = query.Where(c => contractTypes.Contains(c.Type));
        }

        // Order by creation date for consistent pagination
        query = query.OrderByDescending(c => c.CreatedAt);

        // Pagination
        if (skip.HasValue)
        {
            query = query.Skip(skip.Value);
        }

        if (take.HasValue)
        {
            query = query.Take(take.Value);
        }

        var entities = await query.AsNoTracking().ToListAsync(cancellationToken);
        return entities.Select(MapToArkContractEntity).ToList();
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
            existing.Metadata = walletEntity.Metadata ?? existing.Metadata;
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
                Metadata = walletEntity.Metadata,
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

    public async Task<bool> UpdateContractActivityState(
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
        ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task<bool> DeleteContract(
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
        ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
        return true;
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
        )
        {
            Metadata = entity.Metadata
        };
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
}
