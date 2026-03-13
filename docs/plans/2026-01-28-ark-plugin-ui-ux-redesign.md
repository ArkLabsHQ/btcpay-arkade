# Ark Plugin UI/UX Redesign

**Date:** 2026-01-28
**Status:** Draft
**Scope:** UI polish, mass-select lists, Store Overview cleanup, dashboard widget, backend optimizations

---

## Overview

This plan addresses UI/UX improvements for the BTCPayServer Ark plugin to match BTCPay's design philosophy while introducing Arkade-specific elements with good UX.

### Goals

1. Add mass-select functionality to all list views
2. Clean up the Store Overview page layout
3. Add Arkade wallet widget to BTCPay dashboard
4. Optimize backend queries and caching

---

## 1. Mass-Select for List Views

### Pattern

Adopt BTCPay's exact mass-action pattern:
- Dual `<thead>` structure (normal header vs action header)
- `data-selected` attribute toggling via JS
- Form-based bulk actions
- Sticky headers that swap visibility based on selection state

### Mass Actions by List

| List | Mass Actions |
|------|--------------|
| **VTXOs** | "Build Intent", "Build Transaction", "Force Refresh State" |
| **Contracts** | "Sync Selected", "Set Active", "Set Inactive", "Set Awaiting Funds" |
| **Swaps** | "Poll Status" |

### Implementation

Create reusable partial `_MassActionTableWrapper.cshtml`:
- Wraps table with form and dual thead
- Accepts action buttons as parameter
- Includes BTCPay's mass-action JS/CSS patterns

Files to modify:
- `Views/Ark/Vtxos.cshtml`
- `Views/Ark/Contracts.cshtml`
- `Views/Ark/Swaps.cshtml`

---

## 2. Sublists with Mass-Select

### Component Hierarchy

```
_MassActionTableWrapper.cshtml    <- Form + dual thead + JS hooks
    _VtxoTableBody.cshtml         <- Just tbody rows (reusable)
    _SwapTableBody.cshtml         <- Just tbody rows (reusable)
    _ContractTableBody.cshtml     <- Just tbody rows (reusable)
```

### Sublist Mass Actions

| Sublist | Mass Actions |
|---------|--------------|
| VTXOs (under Contract) | "Spend Selected", "Build Intent" |
| Swaps (under Contract) | "Poll Status" |

### Column Reduction for Sublists

**VTXOs under Contract:**
- Hide: Script column (implied by parent)
- Show: Outpoint, Amount, Status, Seen date

**Swaps under Contract:**
- Hide: Contract Address column (implied by parent)
- Show: Swap ID, Type, Amount, Status, Created

### Configuration

| Feature | Main List | Sublist |
|---------|-----------|---------|
| Mass-select | Yes | Yes |
| Pagination | Yes | No (show all, max ~20) |
| Filters | Yes | No |
| Column set | Full | Reduced |

---

## 3. Store Overview Cleanup

### Current Problems

1. Quick Actions card has redundant links (View VTXOs, View Intents already in nav)
2. Wallet Status card mixes status display with config actions
3. Wallet Configuration card is separate but related to status
4. Balances row wastes space when only one balance type exists
5. Service Connections is bulky with verbose status display
6. Force Batch modal hides fee/detail information

### New Layout

```
+-----------------------------------------------------------+
| Balances (compact: inline badges when only 1-2 types)     |
+---------------------------+-------------------------------+
| Wallet Info               | Configuration                 |
| - Type, ID, Keys          | - Lightning toggle            |
| - Default Address         | - Auto-Sweep destination      |
|   (for single-key)        | - Sub-dust toggle             |
+---------------------------+-------------------------------+
| Actions (single row)                                      |
| [Transfer] [Sync] [Batch VTXOs...] [Show Key] [Clear]     |
+-----------------------------------------------------------+
| Services (compact inline: * Ark Operator  * Boltz)        |
| (click to expand for details/errors)                      |
+-----------------------------------------------------------+
```

### Changes

- Remove nav-duplicate quick links (View VTXOs, View Intents)
- Merge Wallet Status + Configuration into two side-by-side cards
- Compact balances display when few types present
- Inline service status indicators (expand on click for details/errors)
- "Batch VTXOs" modal shows fee estimate before confirming

### Files to Modify

- `Views/Ark/StoreOverview.cshtml` (major restructure)
- `Views/Ark/_ArkBalances.cshtml` (compact mode)
- `Views/Ark/_ServiceConnections.cshtml` (inline compact mode)

---

## 4. Dashboard Widget

### Purpose

Add compact Arkade wallet info widget to main BTCPay dashboard for quick visibility.

### Layout

```
+-------------------------------------------+
| Arkade Wallet                         [*] |
+-------------------------------------------+
| Available     12,450 sats                 |
| Recoverable      800 sats  [!]            |
+-------------------------------------------+
| * Operator Connected   * Boltz Ready      |
+-------------------------------------------+
| [View Wallet]              [Transfer ->]  |
+-------------------------------------------+
```

### Features

- Shows key balances (available + recoverable if any)
- Compact service status indicators
- Quick action buttons to Ark overview and transfer modal
- Warning indicator if recoverable balance exists (needs attention)
- Gear icon links to Ark configuration

### Visibility Rules

Widget only shows when:
- Store has Ark wallet configured
- User has permission to view wallet

### Implementation

- Create ViewComponent: `Components/ArkDashboardWidget/`
- Register via `AddUIExtension("dashboard", "Ark/DashboardWidget")`
- Direct render (no AJAX lazy loading)

### Files to Create

- `Components/ArkDashboardWidget/ArkDashboardWidget.cs`
- `Components/ArkDashboardWidget/Default.cshtml`
- `Models/ArkDashboardWidgetViewModel.cs`

---

## 5. Technical Backend Improvements

### 5.1 Server Info Caching

**Problem:** `GetServerInfoAsync()` called 22 times across 7 files. Server info (network, dust limit, signer key) rarely changes.

**Solution:** Create `ArkServerInfoCache` service:

```csharp
public class ArkServerInfoCache
{
    private ServerInfo? _cached;
    private DateTimeOffset _expiresAt;
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(5);

    public async Task<ServerInfo> GetServerInfoAsync(CancellationToken ct = default);
    public void Invalidate(); // Call on reconnect or config change
}
```

**Usage:**
- Inject `ArkServerInfoCache` instead of `IClientTransport` for info lookups
- 5-minute cache (configurable)
- Invalidate on wallet setup/clear or connection errors

**Files affected:**
- `Controllers/ArkController.cs` (8 calls)
- `Lightning/ArkLightningClient.cs` (1 call)
- `PaymentHandler/ArkadePaymentMethodHandler.cs` (1 call)
- `Payouts/Ark/ArkAutomatedPayoutProcessor.cs` (1 call)
- `Payouts/Ark/ArkPayoutHandler.cs` (5 calls)
- `Services/ArkadeSpendingService.cs` (1 call)
- `Services/ArkContractInvoiceListener.cs` (2 calls)
- `Wallet/HierarchicalDeterministicAddressProvider.cs` (1 call)
- `Wallet/PluginWalletAdapter.cs` (1 call)
- `Wallet/SingleKeyAddressProvider.cs` (1 call)

### 5.2 BoltzLimitsService -> Move to NArk

**Current location:** `BTCPayServer.Plugins.ArkPayServer/Lightning/BoltzLimitsService.cs`

**New location:** `NArk.Swaps` package

**New components in NArk.Swaps:**

1. `CachedBoltzClient` - Wraps `BoltzClient`, caches `GetPairs` calls:
   ```csharp
   public class CachedBoltzClient : BoltzClient
   {
       private SubmarinePairsResponse? _submarineCache;
       private ReversePairsResponse? _reverseCache;
       private DateTimeOffset _expiresAt;

       // Override pairs methods with caching
       // Delegate all other methods to inner client
   }
   ```

2. `BoltzLimitsValidator` - Validation logic:
   ```csharp
   public class BoltzLimitsValidator
   {
       public (bool IsValid, string? Error) ValidateAmount(
           BoltzLimits limits, long amount, bool isReverse);

       public (bool IsValid, string? Error) ValidateFees(
           BoltzLimits limits, long amount, long actualFee, bool isReverse);
   }
   ```

**Plugin changes:**
- Remove `BoltzLimitsService.cs`
- Use `CachedBoltzClient` and `BoltzLimitsValidator` from NArk.Swaps

### 5.3 Swap Fee Handling

**Status:** Skipped for now

Current behavior (customer pays fee) remains. Configurable fee handling deferred due to complexity with BTCPay's invoice payment matching.

### 5.4 VTXO Storage Query Optimization

**Problem:** Fetching unspent VTXOs by wallet requires either:
1. Fetch ALL unspent VTXOs, then filter by wallet's contracts
2. Fetch ALL contracts for wallet, then fetch VTXOs for those scripts

Both are inefficient for wallets with many contracts.

**Solution:** Add optional `walletIds` parameter to existing `IVtxoStorage` methods:

```csharp
// Updated signatures
Task<IReadOnlyCollection<ArkVtxo>> GetUnspentVtxos(
    string[]? walletIds = null,
    CancellationToken ct = default);

Task<IReadOnlyCollection<ArkVtxo>> GetAllVtxos(
    string[]? walletIds = null,
    CancellationToken ct = default);
```

**Implementation (EfCoreVtxoStorage):**

```csharp
public async Task<IReadOnlyCollection<ArkVtxo>> GetUnspentVtxos(
    string[]? walletIds = null,
    CancellationToken ct = default)
{
    await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

    var query = db.Vtxos.AsQueryable();

    if (walletIds != null)
    {
        var walletScripts = db.WalletContracts
            .Where(c => walletIds.Contains(c.WalletId))
            .Select(c => c.Script);

        query = query.Where(v => walletScripts.Contains(v.Script));
    }

    query = query.Where(v =>
        v.SpentByTransactionId == null &&
        v.SettledByTransactionId == null);

    var entities = await query.ToListAsync(ct);
    return entities.Select(MapToArkVtxo).ToList();
}
```

**Benefits:**
- Backwards compatible (null = current behavior)
- Single DB roundtrip with efficient join
- No loading millions of contracts into memory

**Files to modify:**
- `NArk.Abstractions/VTXOs/IVtxoStorage.cs` (interface)
- `Storage/EfCoreVtxoStorage.cs` (implementation)

---

## Files Summary

### New Files

| File | Purpose |
|------|---------|
| `Views/Ark/_MassActionTableWrapper.cshtml` | Reusable mass-select table wrapper |
| `Views/Ark/_VtxoTableBody.cshtml` | VTXO table body rows |
| `Views/Ark/_SwapTableBody.cshtml` | Swap table body rows |
| `Components/ArkDashboardWidget/ArkDashboardWidget.cs` | Dashboard widget ViewComponent |
| `Components/ArkDashboardWidget/Default.cshtml` | Dashboard widget view |
| `Models/ArkDashboardWidgetViewModel.cs` | Dashboard widget model |
| `Services/ArkServerInfoCache.cs` | Server info caching service |

### Modified Files

| File | Changes |
|------|---------|
| `Views/Ark/StoreOverview.cshtml` | Major layout restructure |
| `Views/Ark/Vtxos.cshtml` | Add mass-select |
| `Views/Ark/Contracts.cshtml` | Add mass-select, update sublists |
| `Views/Ark/Swaps.cshtml` | Add mass-select |
| `Views/Ark/_VtxoTable.cshtml` | Refactor to use body partial |
| `Views/Ark/_SwapTable.cshtml` | Refactor to use body partial |
| `Views/Ark/_ArkBalances.cshtml` | Add compact mode |
| `Views/Ark/_ServiceConnections.cshtml` | Add inline compact mode |
| `Storage/EfCoreVtxoStorage.cs` | Add walletIds parameter |
| `Controllers/ArkController.cs` | Add mass action endpoints, use ArkServerInfoCache |
| `ArkPlugin.cs` | Register new services, dashboard widget |

### NArk Submodule Changes

| File | Changes |
|------|---------|
| `NArk.Swaps/Boltz/CachedBoltzClient.cs` | New - caching wrapper |
| `NArk.Swaps/Boltz/BoltzLimitsValidator.cs` | New - validation logic |
| `NArk.Abstractions/VTXOs/IVtxoStorage.cs` | Add walletIds parameter |

---

## Implementation Order

1. **Backend first:**
   - Server info caching
   - VTXO storage query optimization
   - Move BoltzLimits to NArk

2. **UI components:**
   - Mass-action table wrapper partial
   - Table body partials

3. **List views:**
   - VTXOs list with mass-select
   - Contracts list with mass-select + sublists
   - Swaps list with mass-select

4. **Store Overview:**
   - Layout restructure
   - Compact balances
   - Compact services

5. **Dashboard widget:**
   - ViewComponent
   - Registration

---

## Open Questions

None at this time.
