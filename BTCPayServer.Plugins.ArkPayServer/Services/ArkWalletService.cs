using System.Collections.Concurrent;
using AsyncKeyedLock;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using NArk;
using NArk.Contracts;
using NArk.Services;
using NArk.Models;
using NBitcoin;
using NBitcoin.Secp256k1;
using NArk.Extensions;
using NArk.Services.Abstractions;
using BTCPayServer.Plugins.ArkPayServer.Cache;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities.Enums;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkWalletService(
    TrackedContractsCache activeContractsCache,
    ArkPluginDbContextFactory dbContextFactory,
    AsyncKeyedLocker asyncKeyedLocker,
    IOperatorTermsService operatorTermsService,
    ArkVtxoSynchronizationService arkVtxoSyncronizationService,
    IMemoryCache memoryCache,
    ILogger<ArkWalletService> logger) : IArkadeMultiWalletSigner
{
    
    public async Task<ArkWallet?> GetWallet(string walletId, CancellationToken cancellationToken)
    {
        var wallets = await GetWallets([walletId], cancellationToken);
        return wallets.SingleOrDefault();
        
    }
    public async Task<ArkWallet[]> GetWallets(string[] walletIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ArkWallet>();
        foreach (var walletId in walletIds)
        {
            if (memoryCache.TryGetValue("ark-wallet-" + walletId, out ArkWallet? wallet) && wallet is not null)
            {
                result.Add(walletId, wallet);
            }
        }
        var remaining = walletIds.Except(result.Keys);
        if (remaining.Any())
        {
            await using var dbContext = dbContextFactory.CreateContext();
            var wallets = await dbContext.Wallets.Where(w => remaining.Contains(w.Id)).ToArrayAsync(cancellationToken);
            foreach (var wallet in wallets)
            {
                memoryCache.Set("ark-wallet-" + wallet.Id, wallet);
                result.Add(wallet.Id, wallet);
            }
            
        }
        
        
        return result.Values.ToArray();
    }

    public async Task<decimal> GetBalanceInSats(string walletId, CancellationToken cancellation)
    {
        await using var dbContext = dbContextFactory.CreateContext();

        var contracts = await dbContext.WalletContracts
            .Where(c => c.WalletId == walletId)
            .Select(c => c.Script)
            .ToListAsync(cancellation);

        var sum = await dbContext.Vtxos
            .Where(vtxo => contracts.Contains(vtxo.Script))
            .Where(vtxo => (vtxo.SpentByTransactionId == null || vtxo.SpentByTransactionId == "") && !vtxo.Recoverable)
            .SumAsync(vtxo => vtxo.Amount, cancellationToken: cancellation);


        return sum;
    }

    public async Task<ArkContract> DerivePaymentContract(string walletId, CancellationToken cancellationToken)
    {
        var terms = await operatorTermsService.GetOperatorTerms(cancellationToken);
        return (await DeriveNewContract(walletId, async wallet =>
        {
            var (lastKey, _) = await GetAndIncrementLastIndexUsed(walletId);
            var paymentContract = ContractUtils.DerivePaymentContract(
                new DeriveContractRequest(terms, lastKey.CreateXOnlyPubKey()));
            var address = paymentContract.GetArkAddress();
            var contract = new ArkWalletContract
            {
                WalletId = walletId,
                Active = true,
                ContractData = paymentContract.GetContractData(),
                Script = address.ScriptPubKey.ToHex(),
                Type = paymentContract.Type,

            };

            return (contract, paymentContract);
        }, cancellationToken))!;
    }

    public async Task<ArkContract?> DeriveNewContract(string walletId,
        Func<ArkWallet, Task<(ArkWalletContract NewContractDataEntity, ArkContract NewContract)?>> setup,
        CancellationToken cancellationToken)
    {
        var wallet = await GetWallet(walletId, cancellationToken);
        if (wallet is null)
            throw new InvalidOperationException($"Wallet with ID {walletId} not found.");

        // using var locker = await asyncKeyedLocker.LockAsync($"DeriveNewContract{walletId}", cancellationToken);
        await using var dbContext = dbContextFactory.CreateContext();

        var contract = await setup(wallet);
        if (contract is null)
        {
            throw new InvalidOperationException($"Could not derive contract for wallet {walletId}");
        }

        var result = await dbContext.WalletContracts.Upsert(contract.Value.NewContractDataEntity)
            .RunAndReturnAsync();
        
        if(result.Count != 0)
            logger.LogInformation("New contract derived for wallet {WalletId}: {Script}", walletId,
            contract.Value.NewContractDataEntity.Script);

        activeContractsCache.TriggerUpdate();

        return contract.Value.NewContract;
    }

    public async Task<(ECPrivKey, int)> GetAndIncrementLastIndexUsed(string walletId)
    {
        using var _ = await asyncKeyedLocker.LockAsync($"wallet-lastusedindex-{walletId}");
        await using var dbContext = dbContextFactory.CreateContext();
        var wallet = await dbContext.Wallets.FindAsync(walletId);
        if (wallet is null) throw new InvalidOperationException("Wallet is missing from the database");
        wallet.LatestIndexUsed++;
        dbContext.Entry(wallet).State = EntityState.Modified;
        await dbContext.SaveChangesAsync();
        return (KeyExtensions.GetKeyFromWallet(wallet.Wallet, wallet.LatestIndexUsed), wallet.LatestIndexUsed);
    }
    
    public async Task<string> Upsert(string walletValue, string? destination, bool owner,
        CancellationToken cancellationToken = default)
    {
        // Get main public key
        var publicKey = KeyExtensions.GetXOnlyPubKeyFromWallet(walletValue, -1);
        var terms = await operatorTermsService.GetOperatorTerms(cancellationToken);
        if (destination is not null)
        {
            var addr = ArkAddress.Parse(destination);
            if (!terms.SignerKey.ToBytes().SequenceEqual(addr.ServerKey.ToBytes()))
            {
                throw new InvalidOperationException("Invalid destination server key.");
            }
            
        }

        await using var dbContext = dbContextFactory.CreateContext();
        var wallet = new ArkWallet()
        {
            Id = publicKey.ToHex(),
            WalletDestination = destination,
            Wallet = walletValue,
            WalletType = walletValue switch
            {
                _ when walletValue.StartsWith("nseed") => WalletType.Seed,
                _ when walletValue.StartsWith("nsec") => WalletType.PrivateKey,
                _ => throw new ArgumentOutOfRangeException()
            }
        };
        var commandBuilder = dbContext.Wallets.Upsert(wallet);

        
        if (!owner)
        {
            commandBuilder = commandBuilder.NoUpdate();
        }

        if (await commandBuilder.RunAsync(cancellationToken) > 0)
        {
            memoryCache.Set("ark-wallet-" + publicKey.ToHex(),wallet);
        }
        
        await DeriveNewContract(publicKey.ToHex(), wallet =>
        {
            var contract = ContractUtils.DerivePaymentContract(new DeriveContractRequest(terms, wallet.RequestNewPublicKey(-1)));
            return Task.FromResult<(ArkWalletContract newContractData, ArkContract newContract)?>((new ArkWalletContract
            {
                WalletId = publicKey.ToHex(),
                Active = true,
                ContractData = contract.GetContractData(),
                Script = contract.GetArkAddress().ScriptPubKey.ToHex(),
                Type = contract.Type,
            }, contract));
        }, cancellationToken);
        
        return publicKey.ToHex();
    }

    public async Task ToggleContract(string detailsWalletId, ArkContract detailsContract, bool active)
    {
        await ToggleContract(detailsWalletId, detailsContract.GetArkAddress().ScriptPubKey.ToHex(), active);
    }

    public async Task ToggleContract(string detailsWalletId, string script, bool active)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        var contract = await dbContext.WalletContracts.FirstOrDefaultAsync(w =>
            w.WalletId == detailsWalletId &&
            w.Script == script && w.Active != active);
        if (contract is null)
        {
            return;
        }

        logger.LogInformation("Toggling contract {Script} ({active}) for wallet {WalletId}", script, active,
            detailsWalletId);

        contract.Active = active;
        if (await dbContext.SaveChangesAsync() > 0)
        {
            activeContractsCache.TriggerUpdate();
        }
    }

    public async Task<bool> WalletExists(string walletId, CancellationToken cancellationToken = default)
    {
       return await GetWallet(walletId, cancellationToken) is not null;
    }
    
    [Obsolete("This function was broken down into multiple calls, use that.")]
    public async Task<(Dictionary<ArkWalletContract, VTXO[]>? Contracts, string? Destination, string? Wallet)?> GetWalletInfo(string walletId, bool includeData, CancellationToken cancellationToken = default)
    {

        var wallet = await GetWallet(walletId, cancellationToken);
        if (wallet is null)
        {
            return null;
        }

        Dictionary<ArkWalletContract, VTXO[]>? contracts = null;
        if (includeData)
        {
            var ccc = await GetVTXOsAndContracts([walletId], true, true, cancellationToken);
            ccc.TryGetValue(walletId, out contracts);
        }
        return (contracts, wallet.WalletDestination, includeData ? wallet.Wallet : null);
    }

    public async Task<string?> GetWalletDestination(string walletId, CancellationToken cancellationToken = default)
    {
        var wallet =
            await GetWallet(walletId, cancellationToken);
        return wallet?.WalletDestination;
    }

    public async Task SetWalletDestination(string walletId, string? destination, CancellationToken cancellationToken = default)
    {
        if (destination is not null)
        {
            var terms = await operatorTermsService.GetOperatorTerms(cancellationToken);
            var addr = ArkAddress.Parse(destination);
            if (!terms.SignerKey.ToBytes().SequenceEqual(addr.ServerKey.ToBytes()))
            {
                throw new InvalidOperationException("Invalid destination server key.");
            }
        }

        await using var dbContext = dbContextFactory.CreateContext();
        var wallet = await dbContext.Wallets.FindAsync([walletId], cancellationToken);
        if (wallet is null)
        {
            throw new InvalidOperationException($"Wallet {walletId} not found.");
        }

        wallet.WalletDestination = destination;
        await dbContext.SaveChangesAsync(cancellationToken);
        
        // Update cache
        memoryCache.Set("ark-wallet-" + walletId, wallet);
    }

    public async Task<bool> CanHandle(string walletId, CancellationToken cancellationToken = default)
    {
        return await WalletExists(walletId, cancellationToken);
    }

    public async Task<IArkadeWalletSigner> CreateSigner(string walletId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();

        var (lastKey, lastIndex) = await GetAndIncrementLastIndexUsed(walletId);
        return new MemoryWalletSigner(lastKey);
    }

    public async Task UpdateBalances(string configWalletId, bool onlyActive,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        var wallets = await dbContext.WalletContracts
            .Where(c => c.WalletId == configWalletId && (!onlyActive || c.Active))
            .Select(c => c.Script)
            .ToListAsync(cancellationToken);

        await arkVtxoSyncronizationService.PollScriptsForVtxos(wallets.ToHashSet(), cancellationToken);
    }

    public async Task<(IReadOnlyCollection<ArkWalletContract> Contracts, Dictionary<string, VTXO[]> ContractVtxos)> GetArkWalletContractsAsync(
        string walletId, 
        int skip = 0, 
        int count = 10, 
        string searchText = "", 
        bool? active = null,
        bool includeVtxos = false,
        bool allowSpent = false,
        bool allowNote = false,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();

        var contracts = await dbContext.WalletContracts
            .Include(c => c.Swaps)
            .Where(c => c.WalletId == walletId)
            .Where(c => string.IsNullOrEmpty(searchText) || c.Script.Contains(searchText))
            .Where(c => active == null || c.Active == active)
            .OrderByDescending(c => c.CreatedAt)
            .Skip(skip)
            .Take(count)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var contractVtxos = new Dictionary<string, VTXO[]>();

        if (includeVtxos && contracts.Any())
        {
            var contractScripts = contracts.Select(c => c.Script).ToHashSet();
            
            var vtxos = await dbContext.Vtxos
                .Where(vtxo =>
                    (allowSpent || (vtxo.SpentByTransactionId == null && vtxo.SettledByTransactionId == null))  &&
                    (allowNote || !vtxo.Recoverable) &&
                    contractScripts.Contains(vtxo.Script))
                .OrderByDescending(vtxo => vtxo.SeenAt)
                .ToArrayAsync(cancellationToken);

            contractVtxos = vtxos
                .GroupBy(v => v.Script)
                .ToDictionary(g => g.Key, g => g.ToArray());
        }

        return (contracts, contractVtxos);
    }

    public async Task<IReadOnlyCollection<ArkSwap>> GetArkWalletSwapsAsync(
        string walletId,
        int skip = 0,
        int count = 10,
        string searchText = "",
        ArkSwapStatus? status = null,
        ArkSwapType? swapType = null,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();

        var swaps = await dbContext.Swaps
            .Include(s => s.Contract)
            .Where(s => s.WalletId == walletId)
            .Where(s => string.IsNullOrEmpty(searchText) || 
                        s.SwapId.Contains(searchText) || 
                        s.Invoice.Contains(searchText) ||
                        s.Hash.Contains(searchText))
            .Where(s => status == null || s.Status == status)
            .Where(s => swapType == null || s.SwapType == swapType)
            .OrderByDescending(s => s.CreatedAt)
            .Skip(skip)
            .Take(count)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return swaps;
    }

    public async Task<IReadOnlyCollection<VTXO>> GetArkWalletVtxosAsync(
        string walletId,
        int skip = 0,
        int count = 10,
        string searchText = "",
        bool includeSpent = false,
        bool includeRecoverable = false,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();

        // Get contract scripts for this wallet
        var contractScripts = await dbContext.WalletContracts
            .Where(c => c.WalletId == walletId)
            .Select(c => c.Script)
            .ToListAsync(cancellationToken);

        var vtxos = await dbContext.Vtxos
            .Where(v => contractScripts.Contains(v.Script))
            .Where(v => string.IsNullOrEmpty(searchText) || 
                        v.TransactionId.Contains(searchText) ||
                        v.Script.Contains(searchText))
            .Where(v => includeSpent || (v.SpentByTransactionId == null && v.SettledByTransactionId == null))
            .Where(v => includeRecoverable || !v.Recoverable)
            .OrderByDescending(v => v.SeenAt)
            .Skip(skip)
            .Take(count)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return vtxos;
    }

    public async Task<IReadOnlyCollection<ArkIntent>> GetArkWalletIntentsAsync(
        string walletId,
        int skip = 0,
        int count = 10,
        string searchText = "",
        ArkIntentState? state = null,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();

        var intents = await dbContext.Intents
            .Where(i => i.WalletId == walletId)
            .Where(i => string.IsNullOrEmpty(searchText) || 
                        (i.IntentId != null && i.IntentId.Contains(searchText)) ||
                        (i.BatchId != null && i.BatchId.Contains(searchText)) ||
                        (i.CommitmentTransactionId != null && i.CommitmentTransactionId.Contains(searchText)))
            .Where(i => state == null || i.State == state)
            .OrderByDescending(i => i.CreatedAt)
            .Skip(skip)
            .Take(count)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return intents;
    }

    // public async Task<IReadOnlyCollection<ArkWalletContract>> GetArkWalletContractsAsync(
    //     string[]? walletIds, 
    //     int skip = 0, 
    //     int count = 10, 
    //     string searchText = "", 
    //     bool? active = null, 
    //     CancellationToken cancellationToken = default)
    // {
    //     await using var dbContext = dbContextFactory.CreateContext();
    //
    //     return await dbContext.WalletContracts
    //         .Include(c => c.Swaps)
    //         .Where(c => walletIds == null || walletIds.Contains(c.WalletId))
    //         .Where(c => string.IsNullOrEmpty(searchText) || c.Script.Contains(searchText))
    //         .Where(c => active == null || c.Active == active)
    //         .OrderByDescending(c => c.CreatedAt)
    //         .Skip(skip)
    //         .Take(count)
    //         .AsNoTracking()
    //         .ToListAsync(cancellationToken);
    // }

    public async Task<Dictionary<string, Dictionary<ArkWalletContract, VTXO[]>>> GetVTXOsAndContracts(
        string[]? walletIds, 
        bool allowSpent, 
        bool allowNote, 
        CancellationToken cancellationToken)
    {
        return await GetVTXOsAndContracts(walletIds, allowSpent, allowNote, null, null, null, cancellationToken);
    }

    /// <summary>
    /// Get VTXOs and contracts for specified wallets, optionally filtered by specific VTXO outpoints, search text, and active status
    /// </summary>
    public async Task<Dictionary<string, Dictionary<ArkWalletContract, VTXO[]>>> GetVTXOsAndContracts(
        string[]? walletIds, 
        bool allowSpent, 
        bool allowNote, 
        HashSet<OutPoint>? vtxoOutpoints,
        string? searchText = null,
        bool? active = null,
        CancellationToken cancellationToken = default)
    {
        // Optimize for single wallet case - use the new efficient method
        if (walletIds?.Length == 1 && vtxoOutpoints == null)
        {
            var walletId = walletIds[0];
            var (contractsx, contractVtxos) = await GetArkWalletContractsAsync(
                walletId,
                skip: 0,
                count: int.MaxValue, // Get all contracts for this use case
                searchText ?? "",
                active,
                includeVtxos: true,
                allowSpent,
                allowNote,
                cancellationToken);

            // Convert to the expected return format
            var result = new Dictionary<string, Dictionary<ArkWalletContract, VTXO[]>>();
            if (contractsx.Any())
            {
                var contractDict = new Dictionary<ArkWalletContract, VTXO[]>();
                foreach (var contract in contractsx)
                {
                    contractDict[contract] = contractVtxos.TryGetValue(contract.Script, out var vtxosx) 
                        ? vtxosx
                        : [];
                }
                result[walletId] = contractDict;
            }
            return result;
        }

        // Multi-wallet or outpoint filtering - use original implementation
        await using var dbContext = dbContextFactory.CreateContext();

        // Get contracts first (no duplication)
        var contractsQuery = dbContext.WalletContracts
            .Include(c => c.Swaps)
            .Where(c => walletIds == null || walletIds.Contains(c.WalletId))
            .Where(c => string.IsNullOrEmpty(searchText) || c.Script.Contains(searchText))
            .Where(c => active == null || c.Active == active)
            .OrderByDescending(c => c.CreatedAt);

        var contracts = await contractsQuery.ToArrayAsync(cancellationToken);

        if (contracts.Length == 0)
            return [];

        // Get VTXOs that match any of the contract scripts
        var contractScripts = contracts.Select(c => c.Script).ToHashSet();
        
        var vtxosQuery = dbContext.Vtxos
            .Where(vtxo =>
                (allowSpent || (vtxo.SpentByTransactionId == null  && vtxo.SettledByTransactionId == null)) &&
                (allowNote || !vtxo.Recoverable) &&
                contractScripts.Contains(vtxo.Script));
        
        // Filter by specific VTXO outpoints if provided
        if (vtxoOutpoints != null && vtxoOutpoints.Count > 0)
        {
            // Convert outpoints to a format we can query
            var outpointPairs = vtxoOutpoints
                .Select(op => $"{op.Hash}{op.N}")
                .ToHashSet();
            
            vtxosQuery = vtxosQuery.Where(vtxo => outpointPairs.Contains(vtxo.TransactionId + vtxo.TransactionOutputIndex));
        }
        
        var vtxos = await vtxosQuery
            .OrderByDescending(vtxo => vtxo.SeenAt)
            .ToArrayAsync(cancellationToken);

        // Join in memory and create nested dictionary structure
        var contractLookup = contracts.ToLookup(c => c.Script);

        return vtxos
            .Where(vtxo => contractLookup.Contains(vtxo.Script))
            .SelectMany(vtxo => contractLookup[vtxo.Script].Select(contract => new { Vtxo = vtxo, Contract = contract }))
            .GroupBy(x => x.Contract.WalletId)
            .ToDictionary(
                walletGroup => walletGroup.Key,
                walletGroup => walletGroup
                    .GroupBy(x => x.Contract)
                    .ToDictionary(
                        contractGroup => contractGroup.Key,
                        contractGroup => contractGroup.Select(x => x.Vtxo).ToArray()
                    )
            );
    }

    public event EventHandler<string>? WalletPolicyChanged;

    public async Task<ArkWallet[]> GetWalletsWithPolicies(CancellationToken cancellationToken = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        var wallets = await dbContext.Wallets
            .Where(w => w.IntentSchedulingPolicy != null && w.IntentSchedulingPolicy != "")
            .ToArrayAsync(cancellationToken);
        
        // Cache them
        foreach (var wallet in wallets)
        {
            memoryCache.Set("ark-wallet-" + wallet.Id, wallet);
        }
        
        return wallets;
    }

    public async Task UpdateWalletIntentSchedulingPolicy(string walletId, string? policyJson, CancellationToken cancellationToken = default)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        
        var wallet = await dbContext.Wallets.FindAsync([walletId], cancellationToken);
        if (wallet == null)
        {
            logger.LogWarning("Wallet {WalletId} not found, cannot update intent scheduling policy", walletId);
            return;
        }
        
        wallet.IntentSchedulingPolicy = policyJson;
        await dbContext.SaveChangesAsync(cancellationToken);
        
        // Invalidate cache
        memoryCache.Remove("ark-wallet-" + walletId);
        
        logger.LogDebug("Updated intent scheduling policy for wallet {WalletId}", walletId);
        
        // Notify listeners of policy change
        WalletPolicyChanged?.Invoke(this, walletId);
    }
}
