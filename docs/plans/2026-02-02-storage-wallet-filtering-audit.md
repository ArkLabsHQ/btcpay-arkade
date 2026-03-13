# Storage Layer Wallet Filtering Audit Plan

**Date:** 2026-02-02
**Status:** ✅ Critical fixes implemented
**Priority:** High (Security)

## Executive Summary

This plan addresses wallet isolation issues in the BTCPayServer Arkade Plugin's storage layer. The audit identified **2 critical security issues** and several design concerns where wallet ID filtering is missing or optional, potentially allowing cross-wallet data access in multi-tenant scenarios.

---

## Critical Issues to Fix

### Issue 1: CancelIntent Cross-Wallet Vulnerability

**File:** `BTCPayServer.Plugins.ArkPayServer/Controllers/ArkController.cs`
**Line:** ~1947
**Method:** `CancelIntent(string storeId, string intentTxId)`

**Problem:** Intent is retrieved by `IntentTxId` without filtering by wallet:
```csharp
var intents = await intentStorage.GetIntents(intentTxIds: [intentTxId], cancellationToken: cancellationToken);
```

**Impact:** An attacker with store access can cancel intents from ANY wallet if they know the IntentTxId.

**Fix Required:**
```csharp
var intents = await intentStorage.GetIntents(
    walletIds: [config.WalletId],
    intentTxIds: [intentTxId],
    cancellationToken: cancellationToken);
```

---

### Issue 2: Contracts Page Information Disclosure

**File:** `BTCPayServer.Plugins.ArkPayServer/Controllers/ArkController.cs`
**Line:** ~1548
**Method:** `Contracts(string storeId, ...)`

**Problem:** All active contracts from ALL wallets are loaded into `CachedContractScripts`:
```csharp
CachedContractScripts = (await contractStorage.GetContracts(isActive: true, cancellationToken: HttpContext.RequestAborted))
    .Select(c => c.Script).ToHashSet()
```

**Impact:** UI caches scripts from all wallets, potentially exposing contract information.

**Fix Required:**
```csharp
CachedContractScripts = (await contractStorage.GetContracts(
    walletIds: [config.WalletId],
    isActive: true,
    cancellationToken: HttpContext.RequestAborted))
    .Select(c => c.Script).ToHashSet()
```

---

## Design Improvements

### 1. Add walletId to `GetIntentVtxosByIntentTxIdsAsync`

**File:** `BTCPayServer.Plugins.ArkPayServer/Storage/EfCoreIntentStorage.cs`

**Current signature:**
```csharp
Task<Dictionary<string, ArkIntentVtxo[]>> GetIntentVtxosByIntentTxIdsAsync(
    IEnumerable<string> intentTxIds,
    CancellationToken cancellationToken = default)
```

**Updated signature:**
```csharp
Task<Dictionary<string, ArkIntentVtxo[]>> GetIntentVtxosByIntentTxIdsAsync(
    string walletId,
    IEnumerable<string> intentTxIds,
    CancellationToken cancellationToken = default)
```

### 2. Add walletId to `DeactivateAwaitingContractsByScript`

**File:** `submodules/NNark/NArk.Abstractions/Contracts/IContractStorage.cs`

**Current signature:**
```csharp
Task<int> DeactivateAwaitingContractsByScript(string script, CancellationToken cancellationToken = default);
```

**Updated signature:**
```csharp
Task<int> DeactivateAwaitingContractsByScript(string walletId, string script, CancellationToken cancellationToken = default);
```

**Note:** This requires updating `VtxoSynchronizationService` in NNark as well.

---

## Storage Interfaces Summary

### IVtxoStorage
| Method | Has WalletId Filter | Assessment |
|--------|---------------------|------------|
| `UpsertVtxo(vtxo)` | No (uses script) | OK - VTXOs are wallet-agnostic by design |
| `GetVtxos(scripts?, outpoints?, walletIds?, ...)` | Optional | ⚠️ Always pass walletIds when user-facing |

### IContractStorage
| Method | Has WalletId Filter | Assessment |
|--------|---------------------|------------|
| `GetContracts(walletIds?, ...)` | Optional | ⚠️ Always pass walletIds when user-facing |
| `SaveContract(entity)` | Via entity | ✓ OK |
| `DeactivateAwaitingContractsByScript(script)` | No | ⚠️ Needs walletId parameter |
| Plugin methods (`GetFirstActiveContractAsync`, etc.) | Yes | ✓ OK |

### IIntentStorage
| Method | Has WalletId Filter | Assessment |
|--------|---------------------|------------|
| `SaveIntent(walletId, intent)` | Yes | ✓ OK |
| `GetIntents(walletIds?, intentTxIds?, ...)` | Optional | ⚠️ Always pass walletIds when user-facing |
| `GetIntentVtxosByIntentTxIdsAsync(intentTxIds)` | No | ⚠️ Needs walletId parameter |
| Plugin methods (`GetLockedVtxoOutpointsAsync`, etc.) | Yes | ✓ OK |

### ISwapStorage ✅ Enhanced & Simplified
| Method | Has WalletId Filter | Assessment |
|--------|---------------------|------------|
| `SaveSwap(walletId, swap)` | Yes | ✓ OK |
| `GetSwaps(walletId?, swapIds?, active?, swapType?, status?, contractScripts?, hash?, invoice?, searchText?, skip?, take?, ...)` | Optional | ✓ Comprehensive filters - use `swapIds` filter for single swap lookups |
| `GetSwapsWithContracts(...)` | Optional | ✓ Same filters as GetSwaps, returns `ArkSwapWithContract` with contract data |
| `UpdateSwapStatus(walletId, swapId, status, failReason?)` | Yes | ✓ Added with wallet guard |

**Removed**: `GetSwap(swapId)` - replaced by `GetSwaps(swapIds: [id])` with proper wallet filtering

**New types**: `ArkSwapWithContract(Swap, Contract?)` - combines swap and contract entity

---

## Implementation Steps

### Step 1: Fix Critical Issues ✅ DONE
- [x] Fix `CancelIntent` to filter by walletId
- [x] Fix `Contracts` view to filter `CachedContractScripts` by walletId
- [x] Add defense-in-depth walletIds to GetVtxos calls in Contracts action
- [x] Add defense-in-depth walletIds to GetVtxos calls in Vtxos action
- [x] Add defense-in-depth walletIds to GetVtxos calls in ArkDashboardWidgetViewComponent

### Step 2: Audit All Storage Calls ✅ DONE
- [x] Search for all calls to `GetContracts()` - ensure walletIds passed
- [x] Search for all calls to `GetIntents()` - ensure walletIds passed
- [x] Search for all calls to `GetVtxos()` - ensure walletIds passed (Note: many calls already filter by wallet-specific scripts)
- [x] Search for all calls to `GetSwaps()` - ensure walletId passed (Note: plugin uses wallet-guarded methods)

### Step 3: Update Interface Signatures ✅ DONE
- [x] Enhanced `ISwapStorage.GetSwaps` with comprehensive filters (swapType, status, contractScripts, hash, invoice, searchText, skip, take)
- [x] Added `ISwapStorage.GetSwapsWithContracts` with same filters, returns `ArkSwapWithContract`
- [x] Added `ISwapStorage.UpdateSwapStatus` with walletId guard and failReason support
- [x] Removed `ISwapStorage.GetSwap(swapId)` - replaced by `GetSwaps(swapIds: [id])` with proper filtering
- [x] Updated `EfCoreSwapStorage` and `InMemorySwapStorage` to implement enhanced interface
- [x] Updated `SwapsManagementService` to use `GetSwaps` with filters instead of removed `GetSwap`
- [x] Removed redundant `UpdateSwapStatusAsync` plugin method (interface method is used instead)
- [x] Updated ArkController call sites to use interface `UpdateSwapStatus` method
- [x] Updated `ArkLightningClient` to use `ISwapStorage` interface instead of `EfCoreSwapStorage`
- [x] Updated `ArkLightningInvoiceListener` to use `ISwapStorage` interface
- [x] Cleaned up `EfCoreSwapStorage` - removed 7 redundant plugin-specific methods (only `GetSwapsWithPaginationAsync` remains for Swaps view)
- [ ] Add walletId to `GetIntentVtxosByIntentTxIdsAsync` (not currently used)
- [ ] Add walletId to `DeactivateAwaitingContractsByScript` (requires NNark changes, architectural decision)

### Step 4: Testing
- [ ] Test intent cancellation with cross-wallet IntentTxId (should fail)
- [ ] Test contracts page doesn't show other wallet scripts
- [ ] Test multi-wallet scenarios
- [ ] Verify cascade deletes don't affect other wallets

---

## Files to Modify

### BTCPayServer.Plugins.ArkPayServer
1. `Controllers/ArkController.cs` - Fix CancelIntent and Contracts methods
2. `Storage/EfCoreIntentStorage.cs` - Add walletId to GetIntentVtxosByIntentTxIdsAsync
3. `Storage/EfCoreContractStorage.cs` - Add walletId to DeactivateAwaitingContractsByScript

### submodules/NNark
1. `NArk.Abstractions/Contracts/IContractStorage.cs` - Update interface
2. `NArk.Core/Services/VtxoSynchronizationService.cs` - Update calls

---

## Code Review Checklist

When reviewing storage layer code:

- [ ] All calls to storage methods that retrieve data pass walletId/walletIds parameter
- [ ] No blind trust of IDs from user input - verify wallet ownership after retrieval
- [ ] Admin-level operations (LoadAllWallets, etc.) are properly authorization-gated
- [ ] New storage methods follow the wallet-guarded pattern

---

## Risk Assessment

| Scenario | Risk Level | Notes |
|----------|------------|-------|
| Multi-tenant BTCPayServer | HIGH | Multiple stores with different wallets share same DB |
| Single-tenant | MEDIUM | Authorization exists but can be bypassed with ID knowledge |
| Self-hosted single user | LOW | Only one wallet, no cross-tenant risk |
