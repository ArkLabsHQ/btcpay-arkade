# Changelog

## [2.0.4] - 2026-03-20

### Features
- **Boarding address support**: HD wallets now include a P2TR boarding address on invoices (above 330 sats) when no on-chain BTC payment method is configured, allowing customers to pay via on-chain Bitcoin with automatic VTXO conversion in the next batch round
- **Boarding configuration**: configurable toggle and minimum threshold (default: enabled, 330 sats) in store settings for HD wallets
- **Confirmation-aware boarding payments**: boarding payments show as "Processing" until 1 confirmation, then upgrade to "Settled"
- **VTXO metadata**: new `Metadata` JSONB column on VTXOs for tracking confirmation state and other per-VTXO data
- **Boarding UTXO polling**: `BoardingUtxoPollService` polls every 30s when unspent boarding VTXOs exist, catching missed NBXplorer events (reconnects, confirmation updates)
- **GitHub Release automation**: CI now creates GitHub Releases with changelog body when a new version is tagged

### Bug Fixes
- Fix `GetArkAddress()` crash on boarding contracts across all views (Contracts, Swaps, VTXOs) — boarding contracts now correctly use `GetOnchainAddress(network)` instead
- Fix boarding VTXO transaction links to use BTCPay's block explorer instead of Arkade explorer
- Fix `NBXplorerBoardingUtxoProvider` to use `GetUnspentUTXOs()` instead of raw UTXO deltas — previously showed already-spent UTXOs as present
- Fix boarding VTXOs not marked as spent after batch rounds — now uses actual `CommitmentTransactionId` from `PostBatchSessionEvent`

### Improvements
- Informative tooltips on store overview: explains why sub-dust is disabled (auto-sweep conflict), boarding behavior, and Lightning unavailability
- Sub-dust toggle visible for all wallet types (previously hidden for nsec wallets)
- Boarding config hidden for nsec/SingleKey wallets (boarding requires HD key derivation)

### SDK
- NNark: add `Metadata` parameter to `ArkVtxo` record
- NNark: add `MetadataJson` column to `VtxoEntity` with EF Core mapping
- NNark: `BoardingUtxoSyncService` tracks confirmation state via VTXO metadata (`Confirmed: True/False`)
- NNark: unconfirmed boarding VTXOs get `ExpiresAt = null` — intent scheduler only batches confirmed ones
- NNark: `PostBatchVtxoPollingHandler` marks unrolled input VTXOs as spent with commitment tx ID
- NNark: new `BoardingUtxoPollService` hosted service for periodic boarding UTXO sync
- NNark: per-package NuGet tagging in CI (`{PackageId}/{Version}`)

## [2.0.3] - 2026-03-19

### Bug Fixes
- Fix Arkade fee estimation: off-chain (Arkade) sends now correctly show 0 fee instead of the batch transaction fee estimate
- Fix BIP21 `ark=` parameter encoding: Ark addresses use bech32m which is already URL-safe — removed unnecessary URL-encoding that broke QR scanning in some wallets
- Fix send form validation: destination parsing no longer blocks the form when amount hasn't been entered yet
- Fix copy button icon (`actions-copy` instead of deprecated `copy`) on store overview and dashboard widget

### Improvements
- Unified QR code rendering: checkout QR now includes `lightning=` parameter for single-QR-code wallets, with proper BIP21 alphanumeric encoding for smaller QR codes
- Register `IGlobalCheckoutModelExtension` so Arkade checkout renders correctly when selected as a global payment method
- Send page UX: dismissible error alerts, compact remove-destination button, amounts shown in BTC instead of sats in balance hints
- Rebrand internal labels from "Ark" to "Arkade" throughout send wizard

### SDK
- NNark: fix `UnknownArkContract` handling in spending and sweeper services (previously crashed on unrecognized contract types)

## [2.0.2] - 2026-03-18

### Bug Fixes
- NNark: fix wallet `LastUsedIndex` regression — re-importing a wallet no longer resets the HD derivation index, preventing address reuse across shared wallets

## [2.0.1] - 2026-03-16

### Features
- Add swap metadata persistence — swaps now store a JSONB `Metadata` column for tracking cross-sign state, refund attempts, and other swap lifecycle data
- Improved QR code generation with proper BIP21 case handling and lightning parameter stripping for compact QR codes

### Bug Fixes
- Make `ValidateStoreAndConfig` async across all controller actions (fixes potential race conditions on config reads)
- Add amount input to destination validation API call so server-side checks can validate against actual invoice amounts

### SDK
- Fix post-spend VTXO polling: use arkd `outpoints` + `spent_only` filter for efficient spent-state verification instead of polling all scripts
- Use `IVtxoStorage` directly for spent-state checks instead of redundant arkd queries
- Fix `Address` and `Metadata` field persistence in `EfCoreSwapStorage`
- Fix missing `VtxosChanged`/`SwapsChanged` event invocations in EF Core storage implementations
- Add REST client transport for arkd HTTP/REST API (alternative to gRPC)
- Add Blazor WASM wallet sample with SqliteWasmBlazor (in-browser SDK)
- Delegation support: automated VTXO delegation to Fulmine delegator services
- Asset delegation E2E tests and shared test helpers

## [2.0.0] - 2026-02-10

### Breaking Changes
- **NNark SDK migration**: All storage implementations (EfCoreVtxoStorage, EfCoreContractStorage, EfCoreIntentStorage, EfCoreSwapStorage, EfCoreWalletStorage) moved from the plugin to the `NArk.Storage.EfCore` NuGet package. Plugin entity classes removed — uses SDK entity types directly.
- **Wallet code moved to SDK**: `WalletFactory`, `WalletType`, HD/SingleKey signers and address providers now live in `NArk.Core`. Plugin wallet adapter layer removed.
- **Plugin renamed**: from "Ark - Beta" to "Arkade - Beta"
- **arkd v0.9.0-rc.0**: requires arkd v0.9+ with split wallet sidecar architecture

### Features
- **Unified Send wizard**: QR scanning, BIP21 URI parsing, fee breakdown display, multi-output support, payout tracking integration, and manual coin selection — all in a single page
- **Activity dashboard widget**: new "Recent Activity" widget showing latest VTXOs, intents, and swaps on the BTCPay dashboard
- **VTXO asset persistence**: migration adding `Assets` JSONB column to track Arkade asset balances per VTXO
- **Intent builder**: new view model and UI for constructing batch intents with multiple outputs
- **Data-sensitive toggle**: amounts displayed in BTC with show/hide toggle for privacy
- **Contract sync on import**: automatically syncs contract state when importing a wallet
- **Clear wallet action**: safely remove wallet configuration from a store without losing on-chain funds
- **Sub-dust amount support**: configurable toggle to accept payments below the 330-sat dust threshold (Ark VTXOs have no dust limit)

### Bug Fixes
- Fix invoice address recycling causing false overpayment detection
- Fix SingleKey wallet `SendToSelf` contract type derivation
- Fix Boltz websocket URL construction and nested mass-select in contracts page
- Fix sweep payment registration and duplicate payment prevention
- Fix legacy sweep handling for pre-migration wallets
- Security: use POST redirect for private key display instead of URL query params
- Replace Newtonsoft `HasConversion` with dual-property JSONB pattern for EF Core compatibility

### SDK
- Swap management: Boltz websocket reconnection fix, improved swap logging
- Batch management: single-stream architecture via `UpdateStreamTopics`
- VHTLC refund descriptor fix and intent locktime calculation
- Asset packet Extension TLV wrapper (OP_RETURN encoding)
- Aspire → nigiri migration for E2E test infrastructure
- Controlled issuance with `AssetRef.FromId` and hex metadata parsing
- Package consolidation: 7 NuGet packages reduced to 3 (NArk, NArk.Core, NArk.Abstractions, NArk.Storage.EfCore, NArk.Swaps)

## [1.0.18] - 2025-12-10

### Features
- **NNark submodule integration**: migrated from bundled NArk library to NNark git submodule (arkade-os/dotnet-sdk)
- **Contract metadata and source tracking**: contracts now track their creation source (e.g., invoice ID) via metadata
- **Major UI/UX redesign**: overhauled contracts, VTXOs, intents, and store overview pages
- **Receive page**: dedicated receive address generation with QR code display
- **Unified IVtxoStorage**: consolidated VTXO query logic with `BuildQuery` pattern

### SDK
- Intent generation loop fix (cancel-regenerate infinite loop prevention)
- Nullable validity filter fix for `InMemoryIntentStorage`
- Shared E2E test infrastructure across NNark
- Package consolidation (7 → 3 packages)

## [1.0.17] - 2025-11-28

### Features
- Show `SettledBy` transaction ID in intent and VTXO views — trace which batch commitment settled a VTXO

## [1.0.16] - 2025-11-19

### Improvements
- Merchant now receives exact invoice amount (previously could receive slightly less due to fee handling)
- Optimize Boltz Lightning payment handling

## [1.0.15] - 2025-11-08

### Bug Fixes
- Fix Boltz fee verification — swap fee validation was rejecting valid swaps due to incorrect fee comparison

## [1.0.14] - 2025-11-05

### Improvements
- Lightning payment UX overhaul: better status tracking, timeout handling, and error messages for Boltz-powered Lightning payments
- Handle LNURL-pay destinations in the send flow

## [1.0.13] - 2025-11-05

### Features
- LNURL-pay support: accept LNURL destinations in the send wizard
- VTXO change subscription for real-time balance updates

### Bug Fixes
- Handle missing Boltz service gracefully (show "unavailable" instead of crashing)
- Sub-dust amount handling for Ark-native payments

## [1.0.12] - 2025-11-01

### Bug Fixes
- Fix Lightning invoice timeout handling with proper duration configuration

## [1.0.11] - 2025-10-31

### Bug Fixes
- Fix UI rendering bug in contract and VTXO list views

## [1.0.10] - 2025-10-30

### Improvements
- UX refinements across contract and VTXO management pages

## [1.0.9] - 2025-10-29

### Bug Fixes
- Fix Boltz swap status detection — swap state machine was not correctly identifying terminal states

## [1.0.8] - 2025-10-28

### Bug Fixes
- Add failsafe error handling around Boltz swap polling to prevent crashes from transient Boltz API errors

## [1.0.7] - 2025-10-27

### Improvements
- Improved database query efficiency for VTXO and contract lookups

## [1.0.6] - 2025-10-26

### Improvements
- Introduce optimized database queries for large VTXO sets, replacing in-memory filtering

## [1.0.5] - 2025-10-25

### Bug Fixes
- Various stability fixes for swap polling and VTXO tracking

## [1.0.4] - 2025-10-24

### Bug Fixes
- Rate-limit swap status polling to prevent hammering the Boltz API during active swaps

## [1.0.3] - 2025-10-23

### Bug Fixes
- Fix active swap detection logic to be more forgiving of transient states
- General stability improvements

## [1.0.2] - 2025-10-22

### Bug Fixes
- More forgiving active swap logic — handle edge cases where swap status is temporarily ambiguous

## [1.0.1] - 2025-10-21

### Bug Fixes
- Add redundant swap status checks to handle missed Boltz websocket events
- Improve swap state machine resilience

## [1.0.0] - 2025-10-21

### Initial Release
- **Ark payment method** for BTCPay Server: accept payments via Arkade virtual UTXOs
- **Lightning via Boltz**: submarine and reverse submarine swaps for Lightning Network payments powered by Boltz exchange
- **Custom checkout UI**: NFC-compatible Arkade checkout component with BIP21 unified QR codes
- **Wallet management**: import wallets via BIP-39 mnemonic (HD) or nostr `nsec` (SingleKey), with private key display
- **Contract management**: derive receive addresses, view active/deactivated contracts, force-sync state
- **VTXO management**: list, filter, and inspect virtual UTXOs with spend status tracking
- **Swap management**: monitor Boltz swap lifecycle with real-time status updates
- **Auto-sweep**: configure a destination address to automatically forward received funds
- **Setup wizard**: guided wallet import and Ark server configuration
- **Dashboard widget**: at-a-glance balance display on the BTCPay dashboard
- **Payout support**: process Ark payouts through BTCPay's payout system
