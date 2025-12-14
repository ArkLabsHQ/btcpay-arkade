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
using NBitcoin.Scripting;
using BTCPayServer.Plugins.ArkPayServer.Cache;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkWalletService(
    TrackedContractsCache activeContractsCache,
    ArkPluginDbContextFactory dbContextFactory,
    IOperatorTermsService operatorTermsService,
    ArkVtxoSynchronizationService arkVtxoSyncronizationService,
    IMemoryCache memoryCache,
    AsyncKeyedLocker<string> asyncKeyedLocker,
    ILogger<ArkWalletService> logger) : IHostedService, IArkadeMultiWalletSigner
{

    private TaskCompletionSource started = new();
    private ConcurrentDictionary<string, IArkadeWalletSigner> walletSigners = new();

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

    public async Task<ArkContract> DerivePaymentContract(string walletId, CancellationToken cancellationToken)
    {
        var terms = await operatorTermsService.GetOperatorTerms(cancellationToken);
        return (await DeriveNewContract(walletId, async wallet =>
        {
            var index = -1;
            
            if (wallet.WalletType == WalletType.Mnemonic)
            {
                index = await GetNextDerivationIndex(walletId, cancellationToken);
            }
            
            var descriptor = HDKeyDerivationService.GetDescriptorAtIndex(wallet.AccountDescriptor, index, terms.Network);
            
            var paymentContract = ContractUtils.DerivePaymentContract(
                new DeriveContractRequest(terms, descriptor, RandomUtils.GetBytes(32)));
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
        Func<ArkWallet, Task<(ArkWalletContract newContractData, ArkContract newContract)?>> setup,
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

        var result = await dbContext.WalletContracts.Upsert(contract.Value.newContractData)
            .RunAndReturnAsync();
        
        if(result.Count != 0)
            logger.LogInformation("New contract derived for wallet {WalletId}: {Script}", walletId,
            contract.Value.newContractData.Script);

        activeContractsCache.TriggerUpdate();

        return contract.Value.newContract;
    }

    /// <summary>
    /// Creates or updates a wallet from an nsec private key.
    /// </summary>
    /// <param name="walletValue">The nsec-encoded private key</param>
    /// <param name="destination">Optional explicit destination address for auto-sweep</param>
    /// <param name="owner">If true, allows updating existing wallet; if false, only inserts new</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The wallet ID (public key hex)</returns>
    public async Task<string> Upsert(string walletValue, string? destination, bool owner,
        CancellationToken cancellationToken = default)
    {
        var terms = await operatorTermsService.GetOperatorTerms(cancellationToken);
        
        if (destination is not null)
        {
            var addr = ArkAddress.Parse(destination);
            if (!terms.SignerKey.ToXOnlyPubKey().ToBytes().SequenceEqual(addr.ServerKey.ToBytes()))
            {
                throw new InvalidOperationException("Invalid destination server key.");
            }
        }

        // Determine wallet type and derive wallet ID
        WalletType walletType;
        string walletId;
        string accountDescriptor;
        int? lastUsedIndex = null;

        if (walletValue.StartsWith("nsec", StringComparison.OrdinalIgnoreCase))
        {
            // Single-key wallet from nsec
            walletType = WalletType.Nsec;
            var privKey = KeyExtensions.GetKeyFromWallet(walletValue);
            var pubKey = privKey.CreateXOnlyPubKey();
            walletId = pubKey.ToHex();
            // For nsec wallets, descriptor is just tr(PUBLIC_KEY_HEX)
            accountDescriptor = $"tr({walletId})";
        }
        else if (IsMnemonic(walletValue))
        {
            // HD wallet from mnemonic
            walletType = WalletType.Mnemonic;
            accountDescriptor = HDKeyDerivationService.ComputeAccountDescriptor(walletValue, terms.Network);
            // Use fingerprint from descriptor as wallet ID for HD wallets
            var descriptor = OutputDescriptor.Parse(accountDescriptor.Replace("/*", "/0"), terms.Network);
            walletId = descriptor.WalletId();
            lastUsedIndex = -1; // No keys used yet
        }
        else
        {
            throw new InvalidOperationException("Unsupported wallet format. Expected nsec or mnemonic.");
        }

        await using var dbContext = dbContextFactory.CreateContext();
        var wallet = new ArkWallet
        {
            Id = walletId,
            WalletDestination = destination,
            Wallet = walletValue,
            WalletType = walletType,
            AccountDescriptor = accountDescriptor,
            LastUsedIndex = lastUsedIndex
        };

        var commandBuilder = dbContext.Wallets.Upsert(wallet);
        if (!owner)
        {
            commandBuilder = commandBuilder.NoUpdate();
        }

        if (await commandBuilder.RunAsync(cancellationToken) > 0)
        {
            memoryCache.Set("ark-wallet-" + wallet.Id, wallet);
        }

        // Load signer for the wallet
        await CreateSigner(walletId, cancellationToken);

        return wallet.Id;
    }

    /// <summary>
    /// Checks if the input string is a valid BIP-39 mnemonic.
    /// </summary>
    private static bool IsMnemonic(string input)
    {
        try
        {
            _ = new Mnemonic(input);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // /// <summary>
    // /// Creates or updates an HD wallet with a mnemonic seed phrase.
    // /// </summary>
    // public async Task<string> UpsertHDWallet(
    //     string mnemonicWords,
    //     string? destination,
    //     CancellationToken cancellationToken = default)
    // {
    //     var terms = await operatorTermsService.GetOperatorTerms(cancellationToken);
    //     var accountDescriptor = HDKeyDerivationService.ComputeAccountDescriptor(mnemonicWords, terms.Network);
    //
    //     // Use fingerprint from descriptor as wallet ID for HD wallets
    //     var descriptor = OutputDescriptor.Parse(accountDescriptor.Replace("/*", "/0"), terms.Network);
    //     var walletId = descriptor.WalletId();
    //
    //     if (destination is not null)
    //     {
    //         var addr = ArkAddress.Parse(destination);
    //         if (!terms.SignerKey.ToXOnlyPubKey().ToBytes().SequenceEqual(addr.ServerKey.ToBytes()))
    //         {
    //             throw new InvalidOperationException("Invalid destination server key.");
    //         }
    //     }
    //
    //     await using var dbContext = dbContextFactory.CreateContext();
    //     var wallet = new ArkWallet
    //     {
    //         Id = walletId,
    //         WalletDestination = destination,
    //         Wallet = KeyExtensions.EncodeMnemonic(mnemonicWords),
    //         WalletType = WalletType.Mnemonic,
    //         LastUsedIndex = -1, // No keys used yet
    //         AccountDescriptor = accountDescriptor
    //     };
    //
    //     if (await dbContext.Wallets.Upsert(wallet).RunAsync(cancellationToken) > 0)
    //     {
    //         memoryCache.Set("ark-wallet-" + walletId, wallet);
    //     }
    //
    //     // Don't auto-derive contract for HD wallets on creation
    //     // Contracts will be derived on demand with proper index tracking
    //
    //     return walletId;
    // }

    /// <summary>
    /// Gets the next derivation index for an HD wallet, atomically incrementing it.
    /// </summary>
    public async Task<int> GetNextDerivationIndex(string walletId, CancellationToken cancellationToken)
    {
        using var lockHandle = await asyncKeyedLocker.LockAsync($"index-{walletId}", cancellationToken);
       
        var wallet =  await GetWallet(walletId, cancellationToken);

        if (wallet is null)
        {
            throw new InvalidOperationException("Wallet not found");
        }

        if (wallet.WalletType != WalletType.Mnemonic)
            return -1;

        await using var dbContext = dbContextFactory.CreateContext();
        
        var nextIndex = (wallet.LastUsedIndex ?? -1) + 1;
        wallet.LastUsedIndex = nextIndex;
        dbContext.Attach(wallet).State =  EntityState.Modified;
        await dbContext.SaveChangesAsync(cancellationToken);

        // Update cache
        memoryCache.Set("ark-wallet-" + walletId, wallet);
        return nextIndex;
    }

    /// <summary>
    /// Stores a contract in the database for a given ArkAddress.
    /// Used to track contracts derived for change outputs in HD wallets.
    /// </summary>
    /// <param name="walletId">The wallet ID</param>
    /// <param name="address">The ArkAddress to store</param>
    /// <param name="terms">Operator terms</param>
    /// <param name="active">Whether the contract should be marked as active</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task StoreContractForAddress(string walletId, ArkAddress address, ArkOperatorTerms terms, bool active, CancellationToken cancellationToken)
    {
        await using var dbContext = dbContextFactory.CreateContext();
        
        // Store the contract with minimal data - we use the script as the key
        // The contract data stores the server and exit delay from terms
        var contractEntity = new ArkWalletContract
        {
            WalletId = walletId,
            Script = address.ScriptPubKey.ToHex(),
            Type = ArkPaymentContract.ContractType,
            ContractData = new Dictionary<string, string>
            {
                ["server"] = terms.SignerKey.ToString(),
                ["exit_delay"] = terms.UnilateralExit.Value.ToString()
            },
            Active = active,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await dbContext.WalletContracts.Upsert(contractEntity).RunAsync(cancellationToken);
        
        logger.LogInformation("Stored contract for wallet {WalletId}: {Script} (active: {Active})", 
            walletId, contractEntity.Script, active);
        
        if (active)
        {
            activeContractsCache.TriggerUpdate();
        }
    }
    

    // /// <summary>
    // /// Derives a payment contract for an HD wallet, returning the derivation index used.
    // /// </summary>
    // public async Task<(ArkContract Contract, int DerivationIndex)> DerivePaymentContractForHDWallet(
    //     string walletId,
    //     CancellationToken cancellationToken)
    // {
    //     var wallet = await GetWallet(walletId, cancellationToken)
    //         ?? throw new InvalidOperationException($"Wallet {walletId} not found");
    //
    //     if (wallet.WalletType != WalletType.Mnemonic)
    //         throw new InvalidOperationException("This method is only for HD wallets. Use DerivePaymentContract for nsec wallets.");
    //
    //     var terms = await operatorTermsService.GetOperatorTerms(cancellationToken);
    //     var derivationIndex = await GetNextDerivationIndex(walletId, cancellationToken);
    //     var userKey = HDKeyDerivationService.DerivePublicKeyAtIndex(wallet.AccountDescriptor!, derivationIndex, terms.Network);
    //
    //     var contract = await DeriveNewContract(walletId, w =>
    //     {
    //         var paymentContract = ContractUtils.DerivePaymentContract(
    //             new DeriveContractRequest(terms, userKey, RandomUtils.GetBytes(32)));
    //         var address = paymentContract.GetArkAddress();
    //         var contractEntity = new ArkWalletContract
    //         {
    //             WalletId = walletId,
    //             Active = true,
    //             ContractData = paymentContract.GetContractData(),
    //             Script = address.ScriptPubKey.ToHex(),
    //             Type = paymentContract.Type,
    //         };
    //
    //         return Task.FromResult<(ArkWalletContract, ArkContract)?>((contractEntity, paymentContract));
    //     }, cancellationToken);
    //
    //     return (contract!, derivationIndex);
    // }

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
    public async Task<(Dictionary<ArkWalletContract, VTXO[]>? Contracts, string? Destination, string? Wallet, WalletType WalletType)?> GetWalletInfo(string walletId, bool includeData, CancellationToken cancellationToken = default)
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
        return (contracts, wallet.WalletDestination, includeData ? wallet.Wallet : null, wallet.WalletType);
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
            if (!terms.SignerKey.ToXOnlyPubKey().ToBytes().SequenceEqual(addr.ServerKey.ToBytes()))
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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = dbContextFactory.CreateContext();

        // load all wallets that have a private key as a signer
        var wallets = await dbContext.Wallets.ToDictionaryAsync(w => w.Id, cancellationToken);
        var needsSave = false;
        
        foreach (var wallet in wallets)
        {
            // Migrate existing single-key wallets that don't have an AccountDescriptor set
            if (wallet.Value.WalletType == WalletType.Nsec && string.IsNullOrEmpty(wallet.Value.AccountDescriptor))
            {
                try
                {
                    // For nsec wallets, the wallet ID is the public key hex
                    // Set the descriptor to tr(PUBLIC_KEY_HEX)
                    wallet.Value.AccountDescriptor = $"tr({wallet.Value.Id})";
                    dbContext.Attach(wallet.Value).State = EntityState.Modified;
                    needsSave = true;
                    logger.LogInformation("Migrated wallet {WalletId}: set AccountDescriptor for single-key wallet", wallet.Key);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Failed to migrate AccountDescriptor for wallet {WalletId}", wallet.Key);
                }
            }
            
            memoryCache.Set("ark-wallet-" + wallet.Key, wallet.Value);

            try
            {
              _ = await   CreateSigner(wallet.Key, cancellationToken);
            }
            catch (Exception e)
            {
                // ignored
            }
        }

        if (needsSave)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Saved AccountDescriptor migrations for existing wallets");
        }

        started.SetResult();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        started = new TaskCompletionSource();
        walletSigners = new();
        return Task.CompletedTask;
    }

    public async Task<bool> CanHandle(string walletId, CancellationToken cancellationToken = default)
    {
        await started.Task;

        // Check if nsec wallet is loaded
        if (walletSigners.ContainsKey(walletId))
            return true;

        // Check if HD wallet exists
        var wallet = await GetWallet(walletId, cancellationToken);
        return wallet?.WalletType == WalletType.Mnemonic;
    }

    public async Task<IArkadeWalletSigner> CreateSigner(string walletId, CancellationToken cancellationToken = default)
    {
        if (walletSigners.TryGetValue(walletId, out var privKey))
        {
            return privKey;
        }

        var wallet = await GetWallet(walletId, cancellationToken)
            ?? throw new InvalidOperationException($"Wallet {walletId} not found");

        
        switch (wallet.WalletType)
        {
            case WalletType.Nsec:
                var k = new MemoryNsecWalletSigner(KeyExtensions.GetKeyFromWallet(wallet.Wallet));
                walletSigners.TryAdd(walletId, k);
                return k;
            case WalletType.Mnemonic:
                
                var kl = new MemorySeedWalletSigner(new Mnemonic(wallet.Wallet));
                walletSigners.TryAdd(walletId, kl);
                return kl;
        }

        throw new InvalidOperationException($"Cannot create signer for wallet {walletId}");
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

    public async Task<ArkWallet[]> GetWallets(CancellationToken cancellationToken = default)
    {
        await started.Task;
        
        await using var dbContext = dbContextFactory.CreateContext();
        var wallets = await dbContext.Wallets.ToArrayAsync(cancellationToken);
        
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
