# Intent System Simplification Plan

## Problem Statement

The current intent system has complexity that leads to bugs:
- Multiple intents can exist for the same VTXOs
- Batch failure handling tries to auto-retry, causing state confusion
- Race conditions in intent creation

## Goals

1. **Single Intent Per VTXO**: Never allow overlapping active intents
2. **Simple Batch Failure**: Cancel on failure, don't auto-retry
3. **Always Force**: New intents always cancel overlapping ones (no manual/scheduled distinction)

## Design

### Intent States (unchanged)

```
WaitingToSubmit → WaitingForBatch → BatchInProgress → BatchSucceeded
                                                   → BatchFailed
                                  → Cancelled
```

### Active Intent Definition

An intent is "active" if its state is one of:
- `WaitingToSubmit`
- `WaitingForBatch`
- `BatchInProgress`

### Core Invariant

**No two active intents may share any VTXO.**

When creating a new intent:
1. Find any active intents that share VTXOs with the new intent
2. Cancel them (delete from server if submitted, mark as Cancelled locally)
3. Create the new intent

This is always "force" mode - new intent wins.

### ValidFrom/ValidUntil

Remove usage for immediate intents. Set to `null` or don't pass them.

### Batch Failure Handling

**Current (complex):**
```
BatchFailed → Reset to WaitingToSubmit with IntentId=null
           → IntentSyncService re-registers with new ID
           → May fail again, loops
```

**New (simple):**
```
BatchFailed → State=BatchFailed, keep batchId, done
```

The intent stays in `BatchFailed` state. The VTXOs become available again for:
- User to create new intent via Send flow
- Scheduler to create refresh intent

### DeleteIntent on Server

When we cancel an intent that's been submitted to arkd (`WaitingForBatch` or `BatchInProgress`):
1. Call `clientTransport.DeleteIntent(intent)` - removes from arkd's queue
2. Update local state to `Cancelled` with reason

## Implementation Steps

### Step 1: Add CancelOverlappingIntents helper

**File:** `NArk.Core/Services/IntentGenerationService.cs`

Create a method that:
1. Queries for active intents containing any of the specified VTXOs
2. For each overlapping intent:
   - If state is `WaitingForBatch`: call `DeleteIntent` on server first
   - Mark as `Cancelled` with reason "Superseded by new intent"

### Step 2: Update GenerateIntentFromSpec

**File:** `NArk.Core/Services/IntentGenerationService.cs`

1. Remove the `force` parameter check - always cancel overlapping
2. Call `CancelOverlappingIntents` before creating the new intent
3. Remove wallet-level lock (cancel handles concurrency)
4. Don't set ValidFrom/ValidUntil (or set to null)

### Step 3: Simplify BatchManagementService batch failure

**File:** `NArk.Core/Services/BatchManagementService.cs`

Change `HandleBatchFailedAsync`:
```csharp
// Just mark as failed and done - no auto-retry
await SaveToStorage(intent.IntentId!, arkIntent =>
    arkIntent with
    {
        State = ArkIntentState.BatchFailed,
        CancellationReason = $"Batch failed: {batchEvent.Reason}",
        // Keep BatchId for tracking
        UpdatedAt = DateTimeOffset.UtcNow
    }, cancellationToken);
```

### Step 4: Remove IntentSyncService retry logic

**File:** `NArk.Core/Services/IntentSynchronizationService.cs`

- Only process `WaitingToSubmit` intents
- Remove any re-registration logic for failed intents

### Step 5: Simplify startup cleanup

**File:** `NArk.Core/Services/BatchManagementService.cs`

`LoadActiveIntentsAsync`:
- Stale `BatchInProgress` → Cancel (batch likely completed while down)
- Duplicate detection still useful for migration/cleanup of old data

### Step 6: Update TransactionHelpers

**File:** `NArk.Core/Helpers/TransactionHelpers.cs`

`ConstructAndSubmitArkTransaction` already cancels overlapping intents after success.
Keep this as-is - it's the right behavior for direct Ark transactions.

## Migration

On first startup after upgrade:
1. Find all active intents grouped by VTXOs
2. For each VTXO with multiple intents, keep newest, cancel rest
3. Log warnings for any cleanup performed

(This is already implemented in `LoadActiveIntentsAsync`)

## Testing Scenarios

1. **New intent cancels old**: Create intent, then another for same VTXOs → first cancelled
2. **Batch failure stays failed**: Simulate batch failure → intent stays in BatchFailed
3. **Direct Ark tx cancels intent**: Have pending intent, do direct spend → intent cancelled
4. **Scheduler can override**: Scheduler creates intent for VTXOs with existing intent → old cancelled

## Files to Modify

1. `NArk.Core/Services/IntentGenerationService.cs` - Add cancel helper, simplify generation
2. `NArk.Core/Services/BatchManagementService.cs` - Simple failure handling
3. `NArk.Core/Services/IntentSynchronizationService.cs` - Remove retry logic
4. `NArk.Abstractions/Intents/IIntentStorage.cs` - Maybe remove force param if not needed
