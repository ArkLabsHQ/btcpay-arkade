# NArk to NNark Migration Work Log

## Overview
Migrating BTCPayServer.Plugins.ArkPayServer from old NArk library to new NNark library.

## Decisions Made
- **Branch**: Work on `feature/nnark-migration`, NNark from its master as submodule
- **HashLock Sweep**: Auto-sweep always when preimage is known
- **Old Code**: Remove old NArk library entirely
- **New Wallet Default**: HD/mnemonic, but support nsec import for existing wallets
- **Boltz Service**: Use NNark's SwapsModule instead of custom BoltzService
- **Test Suite**: Deferred (complex due to BTCPay plugin dynamic loading)

---

## Work Log

### Session 1 - Initial Setup

#### Completed
- [x] Created feature branch `feature/nnark-migration`
- [x] Added NNark as git submodule from https://github.com/aarani/NNark
- [x] Removed old NArk/ and NArk.Tests/ directories
- [x] Updated solution file to add NNark projects
- [x] Updated plugin csproj to reference NNark projects

#### .NET 8 Compatibility Fixes (NNark submodule)
NNark originally targeted .NET 9.0, BTCPayServer uses .NET 8.0.

Changes made in NNark submodule:
- Changed all csproj TargetFramework from net9.0 to net8.0
- Downgraded Microsoft.Extensions.* packages from 10.x to 8.x
- Created `NArk/Extensions/HexExtensions.cs` with `ToHexStringLower()` extension method
  (replaces .NET 9's `Convert.ToHexStringLower`)
- Updated all usages across multiple files

Files modified for HexExtensions:
- NArk/Batches/BatchSession.cs
- NArk/Contracts/HashLockedArkPaymentContract.cs
- NArk/Helpers/OutputDescriptorHelpers.cs
- NArk.Swaps/Boltz/IntentGenerationService.cs
- NArk.Swaps/Boltz/BatchManagementService.cs
- NArk.Swaps/Boltz/BoltzSwapsService.cs
- NArk.Swaps/Helpers/OutputDescriptorHelpers.cs

#### Commits
1. NNark submodule: .NET 8.0 compatibility changes
2. Main repo: Submodule addition, old NArk removal, project reference updates

---

### Session 2 - Wallet Abstraction Layer

#### Completed
- [x] Created `Wallet/WalletType.cs` - enum for Legacy vs HD wallets
- [x] Created `Wallet/IPluginWallet.cs` - bridge interface between plugin and NNark
- [x] Created `Wallet/SingleKeySigningEntity.cs` - ISigningEntity for legacy single keys
- [x] Created `Wallet/LegacyNsecWallet.cs` - IPluginWallet wrapper for nsec backwards compat
- [x] Created `Wallet/HdPluginWallet.cs` - IPluginWallet for BIP-39/BIP-86 HD wallets

#### Architecture Notes
- `IPluginWallet` is the bridge between BTCPay plugin wallet management and NNark's `ISigningEntity`
- Legacy wallets (nsec): `GetNewSigningEntity()` always returns same key
- HD wallets: `GetNewSigningEntity()` increments derivation index, returns unique key
- Both use `OutputDescriptor` (BIP-86 format) for taproot addresses

#### Commits
3. Add wallet abstraction layer for NNark migration

---

### Session 3 - EF Core Storage Adapters

#### Completed
- [x] Created `Storage/EfCoreVtxoStorage.cs` - IVtxoStorage implementation
- [x] Created `Storage/EfCoreContractStorage.cs` - IContractStorage implementation
- [x] Created `Storage/EfCoreIntentStorage.cs` - IIntentStorage implementation
- [x] Created `Storage/EfCoreSwapStorage.cs` - ISwapStorage implementation

#### Mapping Notes
- **VTXO**: Direct mapping, ExpiresAtHeight not tracked by plugin
- **Contract**: `Active` field maps to NNark's `Important` field
- **Intent**: InternalId (int) mapped to Guid using deterministic conversion
  - SignerDescriptor needs to be added to entity in future DB migration
- **Swap**: Refunded status maps to Failed (plugin lacks Refunded enum value)
  - FailReason and Address fields need to be added to entity

#### Commits
4. Add EF Core storage adapters for NNark interfaces

---

### Session 4 - Database Schema Updates

#### Completed
- [x] Updated `ArkWallet` entity:
  - Added `WalletType` column (defaults to Legacy)
  - Added `AccountDescriptor` column (for HD wallet xpub)
  - Added `LastUsedIndex` column (for HD key derivation)
  - Removed old NArk namespace imports
- [x] Updated `ArkWalletContract` entity:
  - Added `SigningEntityDescriptor` column
- [x] Updated `ArkIntent` entity:
  - Added `SignerDescriptor` column
- [x] Updated `ArkSwap` entity:
  - Added `Address` column
  - Added `FailReason` column
- [x] Updated `ArkSwapStatus` enum:
  - Added `Refunded` status
  - Added `IsCompleted()` extension method
- [x] Updated storage adapters to use new fields

#### Commits
5. Add database columns for wallet types and descriptors

---

## Current Status

### Build Status
- NNark library: **COMPILES** (all 6 projects)
- Plugin: **~80+ ERRORS** (old namespace references need updating)

### Remaining Tasks

#### Phase 3: EF Core Storage Adapters
- [ ] `Storage/EfCoreVtxoStorage.cs` - IVtxoStorage impl
- [ ] `Storage/EfCoreContractStorage.cs` - IContractStorage impl
- [ ] `Storage/EfCoreIntentStorage.cs` - IIntentStorage impl
- [ ] `Storage/EfCoreSwapStorage.cs` - ISwapStorage impl

#### Phase 4: Database Migration
- [ ] Add WalletType column to ArkWallet
- [ ] Add AccountDescriptor column to ArkWallet
- [ ] Add LastUsedIndex column to ArkWallet
- [ ] Add SigningEntityDescriptor column to ArkWalletContract

#### Phase 5: Service Migration
- [ ] Refactor ArkWalletService for HD + legacy support
- [ ] Replace BoltzService with NNark SwapsModule
- [ ] Update ArkadeSpender to use NNark SpendingService
- [ ] Fix namespace imports across all files

#### Phase 6: Auto-Sweep
- [ ] Create `Sweep/HashLockedAutoSweepPolicy.cs`

#### Phase 7: UI Updates
- [ ] Update InitialSetup.cshtml for HD wallet creation
- [ ] Create ShowMnemonic.cshtml for recovery phrase display
- [ ] Update StoreOverview.cshtml with wallet type indicator

---

## Type Mapping Reference

| Old NArk | New NNark | Notes |
|----------|-----------|-------|
| `NArk.Services.Abstractions` | `NArk.Abstractions` | Namespace change |
| `NArk.Boltz` | `NArk.Swaps.Boltz` | Moved to Swaps project |
| `IArkadeWalletSigner` | `ISigningEntity` | Interface rename |
| `IOperatorTermsService` | `IClientTransport.GetServerInfoAsync()` | Different pattern |
| `ArkOperatorTerms` | `ArkServerInfo` | Type rename |
| `SpendableArkCoinWithSigner` | TBD | Need to map |
| `ArkTransactionBuilder` | `SpendingService` | Service replacement |
| `BoltzClient` | NNark SwapsModule | Service replacement |
| `BoltzSwapService` | NNark SwapsModule | Service replacement |

---

## Files to Update (Namespace Fixes)

Priority order:
1. Core service files (ArkWalletService, ArkPlugin)
2. Payment handling (ArkadePaymentMethodHandler)
3. Lightning integration (BoltzService, ArkLightningClient)
4. Payout handling
5. Views/UI

---

## Notes

- The migration preserves backwards compatibility with existing wallets
- Existing nsec wallets continue to work with WalletType=Legacy
- New wallets default to HD (BIP-39 mnemonic)
- Contract data format must handle both old pubkey hex and new descriptor formats
