# Arkade plugin e2e test suite — design

**Date:** 2026-05-14
**Branch:** `feat/e2e-ci`
**Status:** approved, awaiting implementation plan

## Goal

Restore comprehensive end-to-end test coverage for the Arkade BTCPay plugin. The deadlock fix on master (`46fe0d4`) unblocked the in-process test harness; a single smoke test (`WalletSetupTests.RegisterAndCreateStore_NavigateToArkWallet_ShowsSetupPage`) currently exists. This spec defines a 30-test suite covering wallet setup, invoice receipt, manual spending, swaps, and payouts at all four coverage levels: pages, Ark-only flows, Boltz Lightning, and chain swap.

## Approach

Restore the structure of the deleted suite (pre-rockstardev-pivot), modernized to the current harness:
- xUnit + `[Collection("Arkade Plugin Tests")]` for serialized execution.
- `UnitTestBase` + `ServerTester` for the in-process BTCPay (already in place via `PlaywrightBaseTest`).
- `appsettings.dev.json:DEBUG_PLUGINS` for plugin discovery (already wired by `ConfigBuilder`).
- Inline Playwright selectors in the BTCPay-`PlaywrightTester` style. No page-object layer; helpers extracted only when 3+ tests repeat the same 5+ lines.
- Five test files matching the plugin's controller layout: `WalletSetupTests`, `ReceiveInvoiceTests`, `SpendingTests`, `SwapsTests`, `PayoutTests`.

## Fixture model

Inherited from the existing smoke test; no changes:
- `SharedPluginTestFixture` (xUnit collection fixture) owns one `ServerTester` per test run.
- Every test creates a fresh admin user + store + (where needed) Ark wallet.
- The 3-min `StartAsync` timeout fence stays in place as a future deadlock canary.

## Test inventory

### `WalletSetupTests.cs` (8 tests)

| # | Name | What it asserts |
|---|------|-----------------|
| 1 | `RegisterAndCreateStore_NavigateToArkWallet_ShowsSetupPage` *(existing)* | Plugin loads, controller route reachable, wizard options render |
| 2 | `CreateNewHotWallet_LandsOnOverview` | "Create a new wallet" path generates an HD wallet, redirects to `/overview` |
| 3 | `ImportNsec_StoresWallet` | Pasting an nsec creates a legacy hot wallet, redirects to `/overview` |
| 4 | `ImportBip39SeedPhrase_StoresHdWallet` | Pasting a 12-word seed creates an HD wallet |
| 5 | `ImportNpub_CreatesTransitoryWallet` | Pasting an npub creates a transitory auto-sweep wallet |
| 6 | `ImportWalletId_ReusesExistingWallet` | Create wallet on store A, then import its id on store B; both stores resolve the same wallet |
| 7 | `InvalidWalletInput_ShowsValidationError` | Garbage input returns to wizard with `Wallet` validation error |
| 8 | `WalletLogDownload_ReturnsFile` | `GET /plugins/ark/stores/{id}/wallet-log` returns 200 + non-empty body |

### `ReceiveInvoiceTests.cs` (5 tests)

| # | Name | What it asserts |
|---|------|-----------------|
| 1 | `CreateInvoice_RendersArkadeCheckoutTab` | BTCPay invoice with Arkade method enabled exposes the Arkade tab + BIP21 |
| 2 | `ArkadeInvoice_PaidViaArkSend_FlipsToSettled` | `docker exec ark-wallet ark send <bip21>` makes the invoice flip to Settled within timeout |
| 3 | `ArkadeInvoice_PaidViaLightning_FlipsToSettled` | LN invoice on the Arkade tab paid by cln/lnd triggers reverse swap; invoice reaches Settled |
| 4 | `ArkadeInvoice_Expiry_FlipsToExpired` | Invoice with short expiry transitions to Expired when unpaid |
| 5 | `Bip21_PreservesThirdPartyParams` | BIP21 with `pj=`, `branta=` params survives the Arkade tab generation (regression for PR #52) |

### `SpendingTests.cs` (8 tests)

| # | Name | What it asserts |
|---|------|-----------------|
| 1 | `SendToArkAddress_Succeeds` | Funded wallet, Send page, paste Ark address, submit → tx broadcast, balance decreases |
| 2 | `SendToLightningInvoice_TriggersSubmarineSwap` | Paste LN invoice → submarine swap appears in Swaps page, completes |
| 3 | `SendToBitcoinAddress_TriggersChainSwap` | Paste BTC address → chain swap (ARK→BTC) appears, completes |
| 4 | `EstimateFees_ReturnsFee` | `POST /estimate-fees` returns a positive fee for a valid destination + amount |
| 5 | `ParseDestination_DetectsAddressType` | `POST /parse-destination` returns `ARK` / `LN` / `BTC` for the three address kinds |
| 6 | `SuggestCoins_ReturnsCoinSelection` | `POST /suggest-coins` returns selected VTXOs covering the requested amount |
| 7 | `MaxAmount_SubtractsEstimatedFee` | Send page Max button → final amount = balance − fee (regression for PR #47) |
| 8 | `SendCrashesGracefully_IfBitcoinCoreUnreachable` | Stop bitcoin container mid-send → controller returns error instead of unhandled exception (regression for PR #48) |

### `SwapsTests.cs` (5 tests)

| # | Name | What it asserts |
|---|------|-----------------|
| 1 | `SwapsPage_ListsSubmittedSwaps` | After creating any swap, `GET /plugins/ark/stores/{id}/swaps` lists it |
| 2 | `SubmarineSwap_ArkToLightning_Completes` | ARK→LN flow reaches `swap.transaction.claimed` |
| 3 | `ReverseSwap_LightningToArk_Completes` | LN→ARK flow reaches `transaction.confirmed` |
| 4 | `ChainSwap_ArkToBitcoin_Completes` | ARK→BTC flow: lockup tx broadcast, claim tx mined, Boltz reports `swap.completed` |
| 5 | `ChainSwap_BitcoinToArk_Completes` | BTC→ARK flow: BTC sent to lockup address, server cross-sign claim posted, store balance reflects inbound VTXO |

### `PayoutTests.cs` (4 tests)

| # | Name | What it asserts |
|---|------|-----------------|
| 1 | `CreatePayout_ToArkAddress_Pending` | BTCPay Greenfield API creates payout, state=`AwaitingApproval` |
| 2 | `ApprovePayout_AutoProcessesViaArkPaymentMethod` | Approving triggers `ArkAutomatedPayoutSender`, state progresses to `InProgress` → `Completed` |
| 3 | `PullPayment_ToArkAddress_EndToEnd` | Pull-payment created, claimed, approved, completed |
| 4 | `PayoutToLightning_TriggersBoltzSubmarine` | Payout with LN destination triggers submarine swap, reaches Completed |

## Helper additions

Added to `PlaywrightBaseTest`:

```csharp
// Wallet setup
Task<string> CreateStoreWithArkWalletAsync(string? walletInput = null);
//   walletInput == null → use "Create a new wallet" path
//   walletInput != null → POST to /initial-setup with the provided string (nsec, seed, npub, wallet-id)
//   returns the storeId

// Funding
Task FundArkWalletAsync(string storeId, long sats = 100_000);
//   1. GET /plugins/ark/stores/{id}/overview to find the receive address
//   2. shell out `docker exec ark-wallet ark send <addr> <sats>`
//   3. poll the overview balance until >= sats or 30s elapses

// API access
Task<HttpClient> AuthenticatedApiClientAsync(string storeId);
//   Creates a server-wide API key with the registered admin's session cookie, returns an HttpClient
//   with the X-Api-Key header set. Used by payout/invoice tests to hit Greenfield endpoints.
```

Inline `Process.Start("docker", "exec ...")` lives in a single `DockerHelpers.Exec(string args)` static method (~10 lines) — surfaces stderr on non-zero exit so test failures point at the docker call rather than at a vague Playwright timeout.

## Funding strategy

Use `docker exec ark-wallet ark send <addr> <sats>` for VTXO funding (matches project-memory pattern for swap tests). Skips boarding + round wait. Pre-existing memory notes ark-wallet holds ≥10M sats, enough for ~20 funded-wallet tests at 500K sats each.

For tests that specifically need boarding UTXOs (`SendToBitcoinAddress_TriggersChainSwap`, BTC→ARK chain swap), use `docker exec bitcoin bitcoin-cli sendtoaddress <addr> <btc>` + `docker exec bitcoin bitcoin-cli -generate <n>`.

## CI implications

- Bump `e2e.yml` job timeout from 20 → 30 min. Boltz + chain swap tests each take 30-60s; the full suite is projected at 15-20 min wall time.
- No new docker services. `submodules/NNark/regtest/start-env.sh` already provides arkd, ark-wallet, boltz, boltz-fulmine, lnd, cln, bitcoin, nbxplorer, esplora.
- Postgres service container stays at port 5432 with `btcpay_e2e_test` DB; ServerTester creates the schema per run via `newDb: true`.

## Out of scope

- Coop refund / co-op-spend edge cases — covered by `NArk.Tests.End2End.Swaps` already.
- VTXO unroll / unilateral exit paths — separate spec.
- Multi-store / multi-user concurrency — separate spec.
- Performance / load tests — out of scope for functional e2e.
