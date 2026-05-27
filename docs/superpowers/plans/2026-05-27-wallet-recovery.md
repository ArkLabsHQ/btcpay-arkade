# Wallet Recovery (unified, all wallet types) — Spec + Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans. Steps use `- [ ]` checkboxes.

**Goal:** A wallet-type-agnostic recovery entry point in the NNark SDK that, given a wallet id, rebuilds **contracts + derivation index + funds (VTXOs) + swap data** — including **legacy script variants** (default *and* delegate VTXO scripts derived against the current *and* every deprecated server signer) so funds locked under prior protocol versions are found. Wire it into the BTCPay plugin so **importing a wallet triggers recovery in the background**, plus a manual "Rescan" action with status.

**Why:** `HdWalletRecoveryService` is HD-only and the consumer must stitch 3 calls (scan + pending-tx + swap scan). Its `IndexerVtxoDiscoveryProvider` derives only ONE script (`ArkPaymentContract(currentSigner, currentExit, …)`) — missing the delegate variant and all `DeprecatedSigners`, unlike `arkade-os/ts-sdk` restore which derives `DefaultVtxo` + `DelegateVtxo` across all `csvTimelocks`. The plugin never invokes recovery at all.

**Repos / branches:**
- NNark (submodule): `feat/wallet-recovery` off `origin/master` (`6a5a027`). SDK PR lands first.
- Plugin (main): `feat/wallet-recovery` off `master`. Re-pins NNark, wires it in. Second PR.

---

## Architecture

```
IWalletRecoveryService.RecoverAsync(walletId, options?, ct) -> WalletRecoveryReport
  └── WalletRecoveryService (NArk.Core/Recovery)
        ├── HD wallet:   HdWalletRecoveryService.ScanAsync         (index gap-scan; persists contracts; bumps LastUsedIndex)
        │                  └── IContractDiscoveryProvider[] (indexer + boarding + boltz-swap)
        ├── SingleKey:   ensure deterministic contract derived + RestoreSwaps([descriptor])
        ├── both:        PendingArkTransactionRecoveryService.FinalizePendingArkTransactionsAsync
        ├── both:        VtxoSynchronizationService.PollScriptsForVtxos (funds)
        └── both:        SwapsManagementService.ScanRecoverableSwapsAsync (audit, for the report)
```

**Legacy-variant fix (the core SDK change):** `IndexerVtxoDiscoveryProvider` derives, per index, the full set ts-sdk checks — for each signer in `{ serverInfo.SignerKey } ∪ serverInfo.DeprecatedSigners.Keys`:
- `ArkPaymentContract(signer, exitDelayForSigner, userDescriptor)` (default vtxo)
- `ArkDelegateContract(signer, exitDelayForSigner, userDescriptor, delegateDescriptor)` when a delegate key is configured (`IDelegateConfig`/options)

It probes the indexer for **all** these scripts and returns **every** matched contract (so legacy/delegate funds are persisted + tracked). `DeprecatedSigners` value (`long`) supplies the exit delay for that signer epoch (verify exact semantics in `RestClientTransport.Info.cs`).

**Plugin wiring:** `ArkController.InitialSetup` already runs a background `Task.Run` sync (lines ~161–189) that only polls *existing* contracts. Insert `walletRecoveryService.RecoverAsync(walletId)` at the front of that job so a freshly imported wallet discovers contracts first, then funds sync. Add a `Rescan` POST + in-memory `RecoveryStatusTracker` surfaced on the overview.

---

## Part A — SDK (NNark)

### Task A1: Variant-aware discovery (legacy)
**Files:** Modify `NArk.Core/Recovery/IndexerVtxoDiscoveryProvider.cs`; Add `NArk.Abstractions/Recovery/RecoveryDelegateConfig.cs` (optional delegate key holder); Test `NArk.Tests/Recovery/IndexerVtxoDiscoveryProviderTests.cs`.
- [ ] Failing unit test: a wallet whose VTXO is under a **deprecated signer**'s payment script is discovered (mock transport returns a hit only for the deprecated-signer script). Assert the returned contract uses the deprecated signer + that index's exit delay.
- [ ] Implement: enumerate `{current signer + DeprecatedSigners}`; build `ArkPaymentContract` for each (and `ArkDelegateContract` when a delegate descriptor is supplied); probe all scripts; return all matched contracts. Keep "any hit ⇒ used".
- [ ] Test green. Commit.

### Task A2: `IWalletRecoveryService` + `WalletRecoveryService` + report
**Files:** Add `NArk.Abstractions/Recovery/IWalletRecoveryService.cs`, `WalletRecoveryReport.cs`; Add `NArk.Core/Recovery/WalletRecoveryService.cs`; Modify `NArk.Core/Hosting/ServiceCollectionExtensions.cs`; Test `NArk.Tests/Recovery/WalletRecoveryServiceTests.cs`.
- [ ] `IWalletRecoveryService.RecoverAsync(string walletId, RecoveryOptions? = null, CancellationToken = default) -> Task<WalletRecoveryReport>`.
- [ ] `WalletRecoveryReport`: `{ WalletType, RecoveryReport? HdScan, int ContractsRecovered, IReadOnlyList<ArkSwap> RestoredSwaps, IReadOnlyList<SwapRecoveryInfo> SwapAudit, IReadOnlyList<string> FinalizedPendingTxIds, int FundsScriptsSynced }`.
- [ ] `WalletRecoveryService`: load wallet → dispatch:
  - HD → `HdWalletRecoveryService.ScanAsync` (covers contracts+index+boltz swaps via providers).
  - SingleKey → derive its default contract if absent + `SwapsManagementService.RestoreSwaps([descriptor])`.
  - both → `FinalizePendingArkTransactionsAsync`, then `PollScriptsForVtxos` for recovered scripts, then `ScanRecoverableSwapsAsync` for the audit.
- [ ] DI: `services.AddSingleton<IWalletRecoveryService, WalletRecoveryService>();`.
- [ ] Unit tests (mock the collaborators) for HD + SingleKey dispatch. Green. Commit.

### Task A3: Real-data E2E
**Files:** Add `NArk.Tests.End2End/WalletRecoveryTests.cs` (NUnit, on `SharedSwapInfrastructure`).
- [ ] Fund an HD wallet; derive a receive contract + settle a VTXO; create a boltz swap (VHTLC contract + SwapData). Snapshot {contracts, LastUsedIndex, balance, swaps}.
- [ ] Wipe local storage (contracts/vtxos/swaps), keep the mnemonic/wallet.
- [ ] `RecoverAsync` → assert contracts back, LastUsedIndex restored, balance/VTXOs back, swap (contract + SwapData) back. Idempotent on a second call.
- [ ] (Mark `[Category]` consistent with the suite; respect `[assembly: NonParallelizable]`.)

### Task A4: Docs + finish SDK PR
- [ ] README usage section + `docs/articles/` recovery article + XML docs (NNark CLAUDE.md mandate).
- [ ] `dotnet test NArk.Tests` green. Commit. Push branch. Open NNark PR. Iterate CI to green.

---

## Part B — Plugin

### Task B1: Re-pin NNark + inject service
- [ ] Bump submodule pointer to the NNark `feat/wallet-recovery` HEAD; `git add submodules/NNark`.
- [ ] Inject `IWalletRecoveryService` into `ArkController` (DI already exposes it via `AddArkCoreServices`).

### Task B2: Import-triggered background recovery + status
**Files:** Modify `Controllers/ArkController.cs`; Add `Services/RecoveryStatusTracker.cs`; register in `ArkPlugin.cs`.
- [ ] `RecoveryStatusTracker`: in-memory per-wallet `{ State (Running|Completed|Failed), startedAt, finishedAt, WalletRecoveryReport?, error? }`.
- [ ] In `InitialSetup`'s background `Task.Run`: set Running → `await walletRecoveryService.RecoverAsync(walletId)` → existing funds poll → set Completed (or Failed). (Recovery before the existing VTXO/boarding sync.)
- [ ] `[HttpPost("stores/{storeId}/rescan")]` → same background recovery; guard `GeneratedByStore`.

### Task B3: Overview status UI
**Files:** Modify `Views/Ark/StoreOverview.cshtml`, `Models/StoreOverviewViewModel.cs`, `Controllers/ArkController.cs` (GET populate).
- [ ] Show recovery status (Running spinner / last report summary / Failed) + a "Rescan wallet" button (HD + SingleKey).
- [ ] Build → 0 errors.

### Task B4: Finish plugin PR
- [ ] Build + unit tests. Commit. Push. Open plugin PR. Iterate CI to green.

---

## Notes / risks
- DI cycles: `WalletRecoveryService` must not be injected into anything resolved while building `PaymentMethodHandlerDictionary`/`CurrencyNameTable` (see `reference_btcpay_plugin_di_cycles`). It's only used by the controller (request-time) + background job — safe.
- Swap E2E is infra-heavy + historically flaky (`SharedSwapInfrastructure`) — keep the recovery E2E focused; rerun push-event run if a known-flaky swap test trips.
- `DeprecatedSigners` `long` semantics — confirm exit-delay vs expiry before A1 impl.
