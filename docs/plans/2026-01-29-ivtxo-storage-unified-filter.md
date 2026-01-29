# IVtxoStorage Unified Filter Refactoring Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace multiple specialized IVtxoStorage query methods with a single `GetVtxos` method that accepts a filter parameter object.

**Architecture:** Create a `VtxoFilter` record with optional filter properties (scripts, walletIds, outpoints, allowSpent, allowRecoverable, pagination). The unified method builds dynamic queries based on which filters are set. Existing callers migrate to use the new filter-based API.

**Tech Stack:** C# records, nullable reference types, EF Core dynamic query building, LINQ expressions.

---

## Current State Analysis

### Existing IVtxoStorage Methods (5 query methods)
```csharp
Task<ArkVtxo?> GetVtxoByOutPoint(OutPoint outpoint, ...)
Task<IReadOnlyCollection<ArkVtxo>> GetVtxosByScripts(IReadOnlyCollection<string> scripts, bool allowSpent = false, ...)
Task<IReadOnlyCollection<ArkVtxo>> GetUnspentVtxos(string[]? walletIds = null, ...)
Task<IReadOnlyCollection<ArkVtxo>> GetAllVtxos(string[]? walletIds = null, ...)
```

### Plugin-specific Methods in EfCoreVtxoStorage (4 additional methods)
```csharp
GetVtxoByOutpointAsync(walletId, txId, index)
GetUnspentVtxosByContractScriptsAsync(scripts)
SumUnspentBalanceByContractScriptsAsync(scripts)
GetVtxosWithPaginationAsync(scripts, skip, count, searchText, includeSpent, includeRecoverable)
GetVtxosByScriptsAndOutpointsAsync(scripts, outpoints, includeSpent, includeRecoverable)
```

### Filter Combinations Actually Used
| Filter Combination | Current Method | Usage |
|-------------------|----------------|-------|
| By outpoint (single) | GetVtxoByOutPoint | BatchManagementService |
| By scripts (unspent only) | GetVtxosByScripts(allowSpent=false) | SpendingService, SweeperService, IntentGenerationService |
| By scripts (all) | GetVtxosByScripts(allowSpent=true) | Rare |
| All unspent | GetUnspentVtxos(null) | IActiveScriptsProvider default impl |
| By wallet (unspent) | GetUnspentVtxos(walletIds) | Plugin UI |
| By wallet (all) | GetAllVtxos(walletIds) | Plugin UI |

---

## Target Design

### New VtxoFilter Record
```csharp
public record VtxoFilter
{
    public IReadOnlyCollection<string>? Scripts { get; init; }
    public IReadOnlyCollection<OutPoint>? Outpoints { get; init; }
    public string[]? WalletIds { get; init; }
    public bool IncludeSpent { get; init; } = false;
    public bool IncludeRecoverable { get; init; } = true;
    public string? SearchText { get; init; }
    public int? Skip { get; init; }
    public int? Take { get; init; }

    public static VtxoFilter Unspent => new();
    public static VtxoFilter All => new() { IncludeSpent = true };
    public static VtxoFilter ByOutpoint(OutPoint outpoint) => new() { Outpoints = [outpoint], IncludeSpent = true };
    public static VtxoFilter ByScripts(IReadOnlyCollection<string> scripts) => new() { Scripts = scripts };
}
```

### New Unified Method
```csharp
Task<IReadOnlyCollection<ArkVtxo>> GetVtxos(VtxoFilter filter, CancellationToken cancellationToken = default);
```

### Migration Strategy
1. Add VtxoFilter record
2. Add GetVtxos method to interface
3. Implement in EfCoreVtxoStorage with dynamic query building
4. Implement in InMemoryVtxoStorage
5. Mark old methods as [Obsolete] with migration hints
6. Update callers one-by-one
7. Remove obsolete methods after all callers migrated

---

## Tasks

### Task 1: Create VtxoFilter Record

**Files:**
- Create: `submodules/NNark/NArk.Abstractions/VTXOs/VtxoFilter.cs`
- Test: `submodules/NNark/NArk.Tests.Unit/VTXOs/VtxoFilterTests.cs` (if tests directory exists)

**Step 1: Write the VtxoFilter record**

```csharp
using NBitcoin;

namespace NArk.Abstractions.VTXOs;

/// <summary>
/// Filter parameters for querying VTXOs.
/// All properties are optional - unset properties don't filter.
/// </summary>
public record VtxoFilter
{
    /// <summary>Filter by script hex strings. If null, no script filter applied.</summary>
    public IReadOnlyCollection<string>? Scripts { get; init; }

    /// <summary>Filter by specific outpoints. If null, no outpoint filter applied.</summary>
    public IReadOnlyCollection<OutPoint>? Outpoints { get; init; }

    /// <summary>Filter by wallet IDs (requires join with contracts table). If null, no wallet filter applied.</summary>
    public string[]? WalletIds { get; init; }

    /// <summary>Include spent VTXOs. Default: false (unspent only).</summary>
    public bool IncludeSpent { get; init; }

    /// <summary>Include recoverable (swept) VTXOs. Default: true.</summary>
    public bool IncludeRecoverable { get; init; } = true;

    /// <summary>Search text for TransactionId or Script. If null, no text search.</summary>
    public string? SearchText { get; init; }

    /// <summary>Number of records to skip (for pagination). If null, no skip.</summary>
    public int? Skip { get; init; }

    /// <summary>Number of records to take (for pagination). If null, no limit.</summary>
    public int? Take { get; init; }

    // Static factory methods for common filter combinations

    /// <summary>Get all unspent VTXOs (default filter).</summary>
    public static VtxoFilter Unspent => new();

    /// <summary>Get all VTXOs including spent.</summary>
    public static VtxoFilter All => new() { IncludeSpent = true };

    /// <summary>Get a specific VTXO by outpoint.</summary>
    public static VtxoFilter ByOutpoint(OutPoint outpoint) =>
        new() { Outpoints = [outpoint], IncludeSpent = true };

    /// <summary>Get unspent VTXOs for specific scripts.</summary>
    public static VtxoFilter ByScripts(IReadOnlyCollection<string> scripts) =>
        new() { Scripts = scripts };

    /// <summary>Get unspent VTXOs for specific scripts (params overload).</summary>
    public static VtxoFilter ByScripts(params string[] scripts) =>
        new() { Scripts = scripts };

    /// <summary>Get VTXOs for specific wallet(s).</summary>
    public static VtxoFilter ByWallet(params string[] walletIds) =>
        new() { WalletIds = walletIds };
}
```

**Step 2: Verify file compiles**

Run: `dotnet build submodules/NNark/NArk.Abstractions/NArk.Abstractions.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add submodules/NNark/NArk.Abstractions/VTXOs/VtxoFilter.cs
git commit -m "feat(vtxo): add VtxoFilter record for unified query parameters"
```

---

### Task 2: Add GetVtxos Method to IVtxoStorage Interface

**Files:**
- Modify: `submodules/NNark/NArk.Abstractions/VTXOs/IVtxoStorage.cs:10-17`

**Step 1: Add the new method signature to interface**

Add after line 11 (after `GetVtxoByOutPoint`):

```csharp
    /// <summary>
    /// Unified VTXO query method with flexible filtering.
    /// </summary>
    Task<IReadOnlyCollection<ArkVtxo>> GetVtxos(VtxoFilter filter, CancellationToken cancellationToken = default);
```

**Step 2: Mark old methods as obsolete**

Add `[Obsolete]` attributes to existing query methods:

```csharp
    [Obsolete("Use GetVtxos(VtxoFilter.ByOutpoint(outpoint)) instead")]
    Task<ArkVtxo?> GetVtxoByOutPoint(OutPoint outpoint, CancellationToken cancellationToken = default);

    [Obsolete("Use GetVtxos(VtxoFilter.ByScripts(scripts) with { IncludeSpent = allowSpent }) instead")]
    Task<IReadOnlyCollection<ArkVtxo>> GetVtxosByScripts(IReadOnlyCollection<string> scripts, bool allowSpent = false
        , CancellationToken cancellationToken = default);

    [Obsolete("Use GetVtxos(VtxoFilter.ByWallet(walletIds)) instead")]
    Task<IReadOnlyCollection<ArkVtxo>> GetUnspentVtxos(string[]? walletIds = null, CancellationToken cancellationToken = default);

    [Obsolete("Use GetVtxos(VtxoFilter.ByWallet(walletIds) with { IncludeSpent = true }) instead")]
    Task<IReadOnlyCollection<ArkVtxo>> GetAllVtxos(string[]? walletIds = null, CancellationToken cancellationToken = default);
```

**Step 3: Update IActiveScriptsProvider default implementation**

The default implementation at line 19-22 should use the new method:

```csharp
    async Task<HashSet<string>> IActiveScriptsProvider.GetActiveScripts(CancellationToken cancellationToken)
    {
        return (await GetVtxos(VtxoFilter.Unspent, cancellationToken)).Select(vtxo => vtxo.Script).ToHashSet();
    }
```

**Step 4: Verify interface compiles (will fail until implementations updated)**

Run: `dotnet build submodules/NNark/NArk.Abstractions/NArk.Abstractions.csproj`
Expected: Build succeeded (interface only, no implementations yet)

**Step 5: Commit**

```bash
git add submodules/NNark/NArk.Abstractions/VTXOs/IVtxoStorage.cs
git commit -m "feat(vtxo): add unified GetVtxos method to IVtxoStorage interface

Mark existing query methods as obsolete with migration hints."
```

---

### Task 3: Implement GetVtxos in InMemoryVtxoStorage

**Files:**
- Modify: `submodules/NNark/NArk.Tests.End2End/TestPersistance/InMemoryVtxoStorage.cs`

**Step 1: Add using directive if needed**

Ensure `using NArk.Abstractions.VTXOs;` is present (it already is).

**Step 2: Fix interface compliance first**

The current InMemoryVtxoStorage has signature mismatches. Fix `GetUnspentVtxos` and `GetAllVtxos` to match interface:

```csharp
    public Task<IReadOnlyCollection<ArkVtxo>> GetUnspentVtxos(string[]? walletIds = null, CancellationToken cancellationToken = default)
    {
        // Note: InMemoryVtxoStorage doesn't track wallet associations, so walletIds is ignored
        return Task.FromResult<IReadOnlyCollection<ArkVtxo>>(_vtxos.Values.Where(v => !v.IsSpent()).ToList());
    }

    public Task<IReadOnlyCollection<ArkVtxo>> GetAllVtxos(string[]? walletIds = null, CancellationToken cancellationToken = default)
    {
        // Note: InMemoryVtxoStorage doesn't track wallet associations, so walletIds is ignored
        return Task.FromResult<IReadOnlyCollection<ArkVtxo>>(_vtxos.Values.ToList());
    }
```

**Step 3: Implement GetVtxos method**

Add after `GetAllVtxos`:

```csharp
    public Task<IReadOnlyCollection<ArkVtxo>> GetVtxos(VtxoFilter filter, CancellationToken cancellationToken = default)
    {
        IEnumerable<ArkVtxo> query = _vtxos.Values;

        // Filter by scripts
        if (filter.Scripts is { Count: > 0 })
        {
            var scriptSet = filter.Scripts.ToHashSet();
            query = query.Where(v => scriptSet.Contains(v.Script));
        }

        // Filter by outpoints
        if (filter.Outpoints is { Count: > 0 })
        {
            var outpointSet = filter.Outpoints.Select(op => op.ToString()).ToHashSet();
            query = query.Where(v => outpointSet.Contains(v.OutPoint.ToString()));
        }

        // Note: WalletIds filter not supported in in-memory implementation (no wallet association tracking)

        // Filter by spent state
        if (!filter.IncludeSpent)
        {
            query = query.Where(v => !v.IsSpent());
        }

        // Filter by recoverable state
        if (!filter.IncludeRecoverable)
        {
            query = query.Where(v => !v.IsRecoverable());
        }

        // Search text filter
        if (!string.IsNullOrEmpty(filter.SearchText))
        {
            query = query.Where(v =>
                v.TransactionId.Contains(filter.SearchText, StringComparison.OrdinalIgnoreCase) ||
                v.Script.Contains(filter.SearchText, StringComparison.OrdinalIgnoreCase));
        }

        // Pagination
        if (filter.Skip.HasValue)
        {
            query = query.Skip(filter.Skip.Value);
        }

        if (filter.Take.HasValue)
        {
            query = query.Take(filter.Take.Value);
        }

        return Task.FromResult<IReadOnlyCollection<ArkVtxo>>(query.ToList());
    }
```

**Step 4: Verify test project compiles**

Run: `dotnet build submodules/NNark/NArk.Tests.End2End/NArk.Tests.End2End.csproj`
Expected: Build succeeded (with obsolete warnings, which is fine)

**Step 5: Commit**

```bash
git add submodules/NNark/NArk.Tests.End2End/TestPersistance/InMemoryVtxoStorage.cs
git commit -m "feat(vtxo): implement GetVtxos in InMemoryVtxoStorage

Also fix interface compliance for GetUnspentVtxos and GetAllVtxos signatures."
```

---

### Task 4: Implement GetVtxos in EfCoreVtxoStorage

**Files:**
- Modify: `BTCPayServer.Plugins.ArkPayServer/Storage/EfCoreVtxoStorage.cs`

**Step 1: Add the GetVtxos implementation**

Add after line 142 (after `GetAllVtxos`):

```csharp
    public async Task<IReadOnlyCollection<ArkVtxo>> GetVtxos(VtxoFilter filter, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.Vtxos.AsQueryable();

        // Filter by scripts
        if (filter.Scripts is { Count: > 0 })
        {
            var scriptSet = filter.Scripts.ToHashSet();
            query = query.Where(v => scriptSet.Contains(v.Script));
        }

        // Filter by outpoints
        if (filter.Outpoints is { Count: > 0 })
        {
            var outpointPairs = filter.Outpoints
                .Select(op => $"{op.Hash}{op.N}")
                .ToHashSet();
            query = query.Where(v => outpointPairs.Contains(v.TransactionId + v.TransactionOutputIndex));
        }

        // Filter by wallet IDs (join with WalletContracts)
        if (filter.WalletIds is { Length: > 0 })
        {
            var walletScripts = db.WalletContracts
                .Where(c => filter.WalletIds.Contains(c.WalletId))
                .Select(c => c.Script);
            query = query.Where(v => walletScripts.Contains(v.Script));
        }

        // Filter by spent state
        if (!filter.IncludeSpent)
        {
            query = query.Where(v =>
                (v.SpentByTransactionId ?? "").Length == 0 &&
                (v.SettledByTransactionId ?? "").Length == 0);
        }

        // Filter by recoverable state
        if (!filter.IncludeRecoverable)
        {
            query = query.Where(v => !v.Recoverable);
        }

        // Search text filter
        if (!string.IsNullOrEmpty(filter.SearchText))
        {
            query = query.Where(v =>
                v.TransactionId.Contains(filter.SearchText) ||
                v.Script.Contains(filter.SearchText));
        }

        // Order by creation date (newest first) for consistent pagination
        query = query.OrderByDescending(v => v.SeenAt);

        // Pagination
        if (filter.Skip.HasValue)
        {
            query = query.Skip(filter.Skip.Value);
        }

        if (filter.Take.HasValue)
        {
            query = query.Take(filter.Take.Value);
        }

        var entities = await query.AsNoTracking().ToListAsync(cancellationToken);
        return entities.Select(MapToArkVtxo).ToList();
    }
```

**Step 2: Verify plugin compiles**

Run: `dotnet build BTCPayServer.Plugins.ArkPayServer/BTCPayServer.Plugins.ArkPayServer.csproj`
Expected: Build succeeded (with obsolete warnings from old method usages)

**Step 3: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Storage/EfCoreVtxoStorage.cs
git commit -m "feat(vtxo): implement GetVtxos in EfCoreVtxoStorage

Dynamic query building with all filter combinations supported."
```

---

### Task 5: Migrate SpendingService to Use New GetVtxos

**Files:**
- Modify: `submodules/NNark/NArk.Core/Services/SpendingService.cs:132`

**Step 1: Update GetAvailableCoins method**

Find line 132:
```csharp
var vtxos = await vtxoStorage.GetVtxosByScripts(contractByScript.Keys, false, cancellationToken);
```

Replace with:
```csharp
var vtxos = await vtxoStorage.GetVtxos(VtxoFilter.ByScripts(contractByScript.Keys.ToList()), cancellationToken);
```

**Step 2: Add using directive if needed**

Ensure the file has `using NArk.Abstractions.VTXOs;` (check if ArkVtxo is already used, the using should be present).

**Step 3: Verify NArk.Core compiles**

Run: `dotnet build submodules/NNark/NArk.Core/NArk.Core.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add submodules/NNark/NArk.Core/Services/SpendingService.cs
git commit -m "refactor(vtxo): migrate SpendingService to use unified GetVtxos"
```

---

### Task 6: Migrate IntentGenerationService to Use New GetVtxos

**Files:**
- Modify: `submodules/NNark/NArk.Core/Services/IntentGenerationService.cs:66`

**Step 1: Find and update the GetVtxosByScripts call**

Find line ~66:
```csharp
await vtxoStorage.GetVtxosByScripts([.. activeContractsByScript.Keys], cancellationToken: token);
```

Replace with:
```csharp
await vtxoStorage.GetVtxos(VtxoFilter.ByScripts(activeContractsByScript.Keys.ToList()), token);
```

**Step 2: Verify NArk.Core compiles**

Run: `dotnet build submodules/NNark/NArk.Core/NArk.Core.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add submodules/NNark/NArk.Core/Services/IntentGenerationService.cs
git commit -m "refactor(vtxo): migrate IntentGenerationService to use unified GetVtxos"
```

---

### Task 7: Migrate SweeperService to Use New GetVtxos

**Files:**
- Modify: `submodules/NNark/NArk.Core/Services/SweeperService.cs`

**Step 1: Find GetVtxosByScripts calls and update them**

Search for `GetVtxosByScripts` in the file and replace each with equivalent `GetVtxos` call:

```csharp
// Old:
await vtxoStorage.GetVtxosByScripts(scripts, false, cancellationToken);
// New:
await vtxoStorage.GetVtxos(VtxoFilter.ByScripts(scripts.ToList()), cancellationToken);
```

**Step 2: Verify NArk.Core compiles**

Run: `dotnet build submodules/NNark/NArk.Core/NArk.Core.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add submodules/NNark/NArk.Core/Services/SweeperService.cs
git commit -m "refactor(vtxo): migrate SweeperService to use unified GetVtxos"
```

---

### Task 8: Migrate BatchManagementService to Use New GetVtxos

**Files:**
- Modify: `submodules/NNark/NArk.Core/Services/BatchManagementService.cs:285`

**Step 1: Find and update GetVtxoByOutPoint call**

Find line ~285:
```csharp
await vtxoStorage.GetVtxoByOutPoint(outpoint, cancellationToken);
```

Replace with:
```csharp
(await vtxoStorage.GetVtxos(VtxoFilter.ByOutpoint(outpoint), cancellationToken)).FirstOrDefault();
```

**Step 2: Verify NArk.Core compiles**

Run: `dotnet build submodules/NNark/NArk.Core/NArk.Core.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add submodules/NNark/NArk.Core/Services/BatchManagementService.cs
git commit -m "refactor(vtxo): migrate BatchManagementService to use unified GetVtxos"
```

---

### Task 9: Migrate SwapsManagementService to Use New GetVtxos

**Files:**
- Modify: `submodules/NNark/NArk.Swaps/Services/SwapsManagementService.cs:249`

**Step 1: Find and update GetVtxosByScripts call**

Find line ~249:
```csharp
await vtxoStorage.GetVtxosByScripts([swapScript], false, cancellationToken);
```

Replace with:
```csharp
await vtxoStorage.GetVtxos(VtxoFilter.ByScripts(swapScript), cancellationToken);
```

**Step 2: Verify NArk.Swaps compiles**

Run: `dotnet build submodules/NNark/NArk.Swaps/NArk.Swaps.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add submodules/NNark/NArk.Swaps/Services/SwapsManagementService.cs
git commit -m "refactor(vtxo): migrate SwapsManagementService to use unified GetVtxos"
```

---

### Task 10: Remove Obsolete Methods from Interface

**Files:**
- Modify: `submodules/NNark/NArk.Abstractions/VTXOs/IVtxoStorage.cs`

**Step 1: Verify all callers migrated**

Run: `grep -r "GetVtxoByOutPoint\|GetVtxosByScripts\|GetUnspentVtxos\|GetAllVtxos" submodules/NNark --include="*.cs" | grep -v "IVtxoStorage.cs" | grep -v "EfCoreVtxoStorage.cs" | grep -v "InMemoryVtxoStorage.cs"`

Expected: No results (all callers migrated)

**Step 2: Remove obsolete methods from interface**

Update `IVtxoStorage.cs` to only contain:

```csharp
using NArk.Abstractions.Scripts;

namespace NArk.Abstractions.VTXOs;

public interface IVtxoStorage : IActiveScriptsProvider
{
    public event EventHandler<ArkVtxo>? VtxosChanged;

    Task<bool> UpsertVtxo(ArkVtxo vtxo, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<ArkVtxo>> GetVtxos(VtxoFilter filter, CancellationToken cancellationToken = default);

    async Task<HashSet<string>> IActiveScriptsProvider.GetActiveScripts(CancellationToken cancellationToken)
    {
        return (await GetVtxos(VtxoFilter.Unspent, cancellationToken)).Select(vtxo => vtxo.Script).ToHashSet();
    }
}
```

**Step 3: Remove obsolete methods from implementations**

Remove `GetVtxoByOutPoint`, `GetVtxosByScripts`, `GetUnspentVtxos`, `GetAllVtxos` from:
- `InMemoryVtxoStorage.cs`
- `EfCoreVtxoStorage.cs` (keep plugin-specific methods in #region)

**Step 4: Verify full solution compiles**

Run: `dotnet build`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add -A
git commit -m "refactor(vtxo): remove obsolete IVtxoStorage query methods

All callers now use unified GetVtxos method with VtxoFilter."
```

---

### Task 11: Update Plugin-Specific Methods to Use GetVtxos Internally

**Files:**
- Modify: `BTCPayServer.Plugins.ArkPayServer/Storage/EfCoreVtxoStorage.cs`

**Step 1: Refactor plugin methods to delegate to GetVtxos**

Update `GetVtxosWithPaginationAsync` to use `GetVtxos`:

```csharp
public async Task<IReadOnlyList<VTXO>> GetVtxosWithPaginationAsync(
    IEnumerable<string> contractScripts,
    int skip = 0,
    int count = 10,
    string? searchText = null,
    bool includeSpent = false,
    bool includeRecoverable = false,
    CancellationToken cancellationToken = default)
{
    var filter = new VtxoFilter
    {
        Scripts = contractScripts.ToList(),
        IncludeSpent = includeSpent,
        IncludeRecoverable = includeRecoverable,
        SearchText = searchText,
        Skip = skip,
        Take = count
    };

    // For VTXO entity results, we still need direct DB access
    // But the filter logic is now centralized
    await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
    return await BuildQuery(db, filter).ToListAsync(cancellationToken);
}
```

Consider adding a private `BuildQuery` helper to avoid duplicating query logic.

**Step 2: Verify plugin compiles**

Run: `dotnet build BTCPayServer.Plugins.ArkPayServer/BTCPayServer.Plugins.ArkPayServer.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Storage/EfCoreVtxoStorage.cs
git commit -m "refactor(vtxo): consolidate EfCoreVtxoStorage query logic"
```

---

### Task 12: Final Verification and Cleanup

**Step 1: Run full build**

Run: `dotnet build`
Expected: Build succeeded with no errors

**Step 2: Run tests**

Run: `dotnet test submodules/NNark/NArk.Tests.End2End/NArk.Tests.End2End.csproj`
Expected: All tests pass

**Step 3: Verify no remaining obsolete method usages**

Run: `grep -r "GetVtxoByOutPoint\|GetVtxosByScripts\|GetUnspentVtxos\|GetAllVtxos" --include="*.cs" .`
Expected: Only implementation files (if any backward compat shims remain)

**Step 4: Final commit if any cleanup needed**

```bash
git add -A
git commit -m "chore(vtxo): final cleanup for IVtxoStorage refactoring"
```

---

## Open Questions / Notes

1. **Backward Compatibility:** The plan marks old methods as `[Obsolete]` first before removal, giving callers time to migrate. If external consumers exist, consider keeping obsolete methods longer.

2. **Performance:** The new `GetVtxos` method with dynamic query building should have similar performance to specialized methods. EF Core generates efficient SQL for conditional Where clauses.

3. **InMemoryVtxoStorage WalletIds:** The in-memory implementation ignores WalletIds filter since it doesn't track wallet associations. This matches existing behavior.

4. **Plugin Entity vs ArkVtxo:** Plugin methods that return `VTXO` entities (not `ArkVtxo` records) still need direct DB access. Consider if those should also be migrated to return `ArkVtxo`.
