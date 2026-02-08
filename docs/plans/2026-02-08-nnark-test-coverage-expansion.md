# NNark Test Coverage Expansion Plan

**Status:** Pending
**Date:** 2026-02-08
**Scope:** `submodules/NNark` (dotnet-sdk)

## Current State

### Existing Tests (~22 test cases across 11 classes)

| Layer | Class | Tests | What's Covered |
|-------|-------|-------|----------------|
| Unit | `ArkAddressTests` | 3 cases | Address parsing/validation |
| Unit | `CheckpointTapScriptTests` | 4 cases | TapScript decode, round-trip |
| Unit | `IntentSynchronizationServiceTests` | 4 | Expiry, validity window, submission |
| Unit | `VHtlcContractTests` | 2 | VHTLC contract creation (CSV lock) |
| E2E | `BatchSessionTests` | 1 | Full batch session with generated intent |
| E2E | `BuilderStyleTests` | 1 | Fluent builder API batch flow |
| E2E | `IntentSchedulerTests` | 1 | Intent scheduling + submission |
| E2E | `NoteTests` | 1 | Note contract in batch |
| E2E | `OnchainTests` | 1 | Collaborative exit |
| E2E | `VtxoSynchronizationTests` | 3 | Vtxo receive, auto-deactivate, send/receive |
| E2E | `SwapManagementServiceTests` | 4 | Submarine, reverse, co-op refund, restore |
| Network | `TransportTests` | 1 | Mainnet gRPC connectivity |

### Recent Changes (Jan-Feb 2026) Without Corresponding Tests

The following production code has been significantly modified or added without matching test coverage:

## Gap Analysis

### 1. Unit Tests Needed (NArk.Tests)

#### 1a. CachingClientTransport
- **File:** `NArk.Core/Transport/CachingClientTransport.cs`
- **Why:** New caching layer around IClientTransport. No tests verify cache hit/miss, expiry, or pass-through behavior.
- **Tests to write:**
  - `CachedServerInfo_ReturnsSameInstanceWithinExpiry`
  - `CachedServerInfo_RefreshesAfterExpiry`
  - `NonCachedMethods_AlwaysCallInner` (RegisterIntent, GetEventStream, etc.)

#### 1b. IntentGenerationService
- **File:** `NArk.Core/Services/IntentGenerationService.cs`
- **Why:** Heavily reworked - subscribes to VtxosChanged, must skip wallets with active pending intents, handles SendToSelf contract derivation. Only E2E coverage exists.
- **Tests to write:**
  - `SkipsWallet_WhenActiveWaitingToSubmitIntentExists`
  - `SkipsWallet_WhenActiveWaitingForBatchIntentExists`
  - `GeneratesIntent_WhenNoActivePendingIntents`
  - `CancelsStaleIntents_BeforeGeneratingNew`
  - `HandlesMultipleWallets_Independently`

#### 1c. SimpleIntentScheduler
- **File:** `NArk.Core/Services/SimpleIntentScheduler.cs`
- **Why:** Core scheduling logic with threshold/height checks. `GetIntentsToSubmit` calls `DeriveContract(SendToSelf)` per wallet. Only tested via E2E.
- **Tests to write:**
  - `ReturnsEmpty_WhenNoVtxosApproachExpiry`
  - `ReturnsIntents_WhenVtxoExpiryWithinThreshold`
  - `ReturnsIntents_WhenBlockHeightWithinThreshold`
  - `DerivesSendToSelfContract_PerWallet`

#### 1d. SpendingService
- **File:** `NArk.Core/Services/SpendingService.cs`
- **Why:** `CanSpendOffchain` logic was corrected, spent VTXO filtering added. No dedicated unit tests.
- **Tests to write:**
  - `CanSpendOffchain_ReturnsFalse_WhenNoUnspentVtxos`
  - `CanSpendOffchain_ReturnsFalse_WhenAmountExceedsBalance`
  - `CanSpendOffchain_ReturnsTrue_WhenSufficientBalance`
  - `Spend_FiltersOutSpentVtxos`
  - `Spend_UsesDefaultCoinSelector`

#### 1e. ContractService
- **File:** `NArk.Core/Services/ContractService.cs`
- **Why:** `DeriveContract` now accepts optional metadata param. Contract import and derivation are critical paths.
- **Tests to write:**
  - `DeriveContract_SendToSelf_CreatesValidContract`
  - `DeriveContract_WithMetadata_AttachesMetadata`
  - `ImportContract_StoresAndReturnsEntity`
  - `ImportContract_MultipleWithSameScript_AllStored`

#### 1f. DefaultCoinSelector
- **File:** `NArk.Core/DefaultCoinSelector.cs`
- **Why:** No tests. Coin selection is critical for correct spending.
- **Tests to write:**
  - `SelectsMinimalCoins_ForExactAmount`
  - `SelectsCoins_WithChangeOutput`
  - `ThrowsOrReturnsEmpty_WhenInsufficientFunds`

#### 1g. CachedBoltzClient
- **File:** `NArk.Swaps/Boltz/Client/CachedBoltzClient.cs`
- **Why:** New caching layer around BoltzClient for pairs/limits. No tests.
- **Tests to write:**
  - `CachesPairsResponse_WithinExpiry`
  - `RefreshesPairsResponse_AfterExpiry`
  - `NonCachedMethods_PassThrough`

#### 1h. BoltzLimitsValidator
- **File:** `NArk.Swaps/Boltz/BoltzLimitsValidator.cs`
- **Why:** New validation for swap amount limits. No tests.
- **Tests to write:**
  - `RejectsAmount_BelowMinimum`
  - `RejectsAmount_AboveMaximum`
  - `AcceptsAmount_WithinRange`

#### 1i. ScriptParser (Swap Restore)
- **File:** `NArk.Swaps/ScriptParser.cs`
- **Why:** Parses scripts for swap restoration. No tests. Parsing correctness is critical.
- **Tests to write:**
  - `ParsesSubmarineSwapScript_Correctly`
  - `ParsesReverseSwapScript_Correctly`
  - `ReturnsNull_ForUnrecognizedScript`

#### 1j. PostBatchVtxoPollingHandler / PostSpendVtxoPollingHandler
- **File:** `NArk.Core/Events/PostBatchVtxoPollingHandler.cs`, `PostSpendVtxoPollingHandler.cs`
- **Why:** New event handlers that trigger VTXO polling after batch success/spend. No tests.
- **Tests to write:**
  - `TriggersPollAfterBatchSuccess`
  - `TriggersPollAfterSpendSuccess`

#### 1k. SweeperService
- **File:** `NArk.Core/Services/SweeperService.cs`
- **Why:** Refactored to remove recovery intent creation. No dedicated unit tests.
- **Tests to write:**
  - `Sweep_CreatesIntents_ForPolicySweepTargets`
  - `Sweep_SkipsAlreadySweptContracts`
  - `ForceRefresh_RespectsInterval`

### 2. Integration / E2E Tests Needed (NArk.Tests.End2End)

#### 2a. Multi-wallet isolation test
- **Why:** IntentGenerationService now handles multiple wallets. No E2E test verifies two wallets generate independent intents without interfering.
- **Test:** `TwoWallets_GenerateIndependentIntents`

#### 2b. VTXO polling after batch
- **Why:** `PostBatchVtxoPollingHandler` is new. No E2E verifies that VTXOs auto-refresh after a batch.
- **Test:** `VtxosRefreshAutomatically_AfterBatchSuccess`

#### 2c. Contract metadata round-trip
- **Why:** Metadata support added to `ArkContractEntity` and `ContractService.DeriveContract`. No test verifies metadata survives storage round-trip.
- **Test:** `ContractMetadata_SurvivesStorageRoundTrip`

#### 2d. Spending with coin selection E2E
- **Why:** SpendingService + DefaultCoinSelector are core but only tested indirectly via `VtxoSynchronizationTests.CanSendAndReceiveBackVtxo`.
- **Test:** `SpendExactAmount_ProducesNoChange` and `SpendPartialAmount_ProducesChange`

### 3. Existing TODO in Tests

- **VHtlcContractTests.cs:7** - "TODO: implement more from: https://github.com/arkade-os/rust-sdk/blob/master/ark-core/src/vhtlc_fixtures/vhtlc.json"
  - Port remaining VHTLC test vectors from rust-sdk

## Implementation Order

### Phase 1: Core Unit Tests (highest value, no infra needed)
1. `IntentGenerationServiceTests` (1b) - Most complex recent changes
2. `SpendingServiceTests` (1d) - Correctness fix needs regression tests
3. `SimpleIntentSchedulerTests` (1c) - Core scheduling logic
4. `DefaultCoinSelectorTests` (1f) - Critical for spending correctness
5. `ContractServiceTests` (1e) - Metadata support

### Phase 2: Caching & Validation Unit Tests
6. `CachingClientTransportTests` (1a)
7. `CachedBoltzClientTests` (1g)
8. `BoltzLimitsValidatorTests` (1h)
9. `ScriptParserTests` (1i)

### Phase 3: Event Handler & Service Unit Tests
10. `PostBatchVtxoPollingHandlerTests` (1j)
11. `SweeperServiceTests` (1k)

### Phase 4: E2E Tests (require infrastructure)
12. Multi-wallet isolation (2a)
13. VTXO auto-polling (2b)
14. Contract metadata round-trip (2c)
15. Spending with coin selection (2d)

### Phase 5: Backlog
16. Port VHTLC fixtures from rust-sdk (existing TODO)

## Testing Patterns to Follow

- **Unit tests:** Use NSubstitute for mocking interfaces (existing pattern in `IntentSynchronizationServiceTests`)
- **E2E noswap tests:** Place in `NArk.Tests.End2End.Core` namespace to share `SharedArkInfrastructure`
- **E2E swap tests:** Place in `NArk.Tests.End2End.Swaps` namespace with separate infra
- **Async assertions:** Use `TaskCompletionSource` + `WaitAsync(TimeSpan)` pattern
- **Event-driven verification:** Subscribe to storage events to detect state transitions

## Estimated Scope

- ~35 new unit test methods across ~10 new test classes
- ~4 new E2E test methods across existing or new test classes
- Total: ~39 new tests, roughly doubling current coverage
