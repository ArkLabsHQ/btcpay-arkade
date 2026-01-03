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

        var query = db.WalletContracts.Where(c => c.Active);

        if (walletIdentifiers != null && walletIdentifiers.Count > 0)
        {
            var walletSet = walletIdentifiers.ToHashSet();
            query = query.Where(c => walletSet.Contains(c.WalletId));
        }

        var entities = await query.ToListAsync(cancellationToken);
        return entities.Select(MapToArkContractEntity).ToHashSet();
    }

    public async Task<IReadOnlySet<ArkContractEntity>> LoadContractsByScripts(
        string[] scripts,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var scriptSet = scripts.ToHashSet();
        var entities = await db.WalletContracts
            .Where(c => scriptSet.Contains(c.Script))
            .ToListAsync(cancellationToken);

        return entities.Select(MapToArkContractEntity).ToHashSet();
    }

    public async Task SaveContract(
        string walletIdentifier,
        ArkContractEntity walletEntity,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await db.WalletContracts.FirstOrDefaultAsync(
            c => c.Script == walletEntity.Script && c.WalletId == walletIdentifier,
            cancellationToken);

        if (existing != null)
        {
            existing.Active = walletEntity.Important;
            existing.Type = walletEntity.Type;
            existing.ContractData = walletEntity.AdditionalData;
        }
        else
        {
            var entity = new ArkWalletContract
            {
                Script = walletEntity.Script,
                WalletId = walletIdentifier,
                Active = walletEntity.Important,
                Type = walletEntity.Type,
                ContractData = walletEntity.AdditionalData,
                CreatedAt = walletEntity.CreatedAt
            };
            db.WalletContracts.Add(entity);
        }

        await db.SaveChangesAsync(cancellationToken);

        ContractsChanged?.Invoke(this, walletEntity);
    }

    private static ArkContractEntity MapToArkContractEntity(ArkWalletContract entity)
    {
        return new ArkContractEntity(
            Script: entity.Script,
            Important: entity.Active,
            Type: entity.Type,
            AdditionalData: entity.ContractData,
            WalletIdentifier: entity.WalletId,
            CreatedAt: entity.CreatedAt
        );
    }
}
