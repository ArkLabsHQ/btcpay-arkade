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

## Reuse from `submodules/NNark/NArk.Tests.End2End/Common/`

NNark's e2e suite already ships a tested docker/cli helper layer. We pull those files in via **linked compilation** so a submodule bump propagates fixes for free — no copy-paste, no cross-repo PR:

```xml
<!-- NArk.E2E.Tests.csproj -->
<ItemGroup>
  <Compile Include="..\submodules\NNark\NArk.Tests.End2End\Common\DockerHelper.cs"
           Link="Common\DockerHelper.cs" />
  <Compile Include="..\submodules\NNark\NArk.Tests.End2End\Common\FulmineLiquidityHelper.cs"
           Link="Common\FulmineLiquidityHelper.cs" />
</ItemGroup>
<ItemGroup>
  <PackageReference Include="CliWrap" Version="3.10.0" />
</ItemGroup>
```

This gives us, for free:
- `DockerHelper.Exec(container, args[])` — generic docker exec
- `DockerHelper.MineBlocks(count)` — bitcoin block generation
- `DockerHelper.SendArkdNoteTo(arkAddress, amountSats)` — funds any Arkade address from the arkd notes pool
- `DockerHelper.CreateArkNote(amountSats)` — note minting
- `DockerHelper.CreateLndInvoice(amtSats, expirySecs)` — LND BOLT11 generation
- `DockerHelper.StopContainer(name)` / `StartContainer(name)` — for `SendCrashesGracefully_IfBitcoinCoreUnreachable`
- `DockerHelper.SetBoltzSwapStatus(swapId, status)` — drive swaps to terminal states without timing dependencies
- `FulmineLiquidityHelper.EnsureArkLiquidity()` — prereq for reverse-swap tests

`FulmineLiquidityHelper` references `SharedSwapInfrastructure.FulmineEndpoint` from NNark. We mirror just that one constant locally (~3 lines) rather than dragging in the NUnit `[SetUpFixture]`. Documented in `Common/SharedSwapEndpoints.cs` shim file.

We do **not** reuse `FundedWalletHelper` — it manages NArk SDK wallet state in-process, which is the opposite of our model (BTCPay owns wallet state). Our funding helper drives `DockerHelper.SendArkdNoteTo` against an address scraped from BTCPay's `/overview` page.

## Helpers added in this project

Just two thin BTCPay-specific helpers on top of NNark's primitives:

```csharp
// In PlaywrightBaseTest:

Task<string> CreateStoreWithArkWalletAsync(string? walletInput = null);
//   walletInput == null → use "Create a new wallet" path
//   walletInput != null → POST to /initial-setup with the provided string
//                         (nsec, BIP-39 seed phrase, npub, or wallet-id)
//   returns the storeId

Task FundStoreArkadeWalletAsync(string storeId, long sats = 100_000);
//   1. scrape Arkade receive address from /plugins/ark/stores/{id}/overview
//   2. await DockerHelper.SendArkdNoteTo(arkAddress, sats)
//   3. poll /overview balance until >= sats or 30s elapses

Task<HttpClient> AuthenticatedApiClientAsync(string storeId);
//   Issues a BTCPay server-scoped API key via Greenfield, returns an HttpClient
//   with X-Api-Key set. Used by payout/invoice tests to hit Greenfield endpoints
//   directly instead of clicking through the UI.
```

## Funding strategy

For VTXO-funded wallets (most spending/swap tests), call `FundStoreArkadeWalletAsync(storeId, 500_000)` — that's a thin BTCPay-aware wrapper over `DockerHelper.SendArkdNoteTo`. Memory notes ark-wallet holds ≥10M sats; with 500K-per-test budget, ~20 funded tests can run before the notes pool needs replenishment.

For boarding-UTXO tests (`BTC→ARK chain swap`), use `DockerHelper.Exec("bitcoin", ["bitcoin-cli", "sendtoaddress", addr, btc])` + `DockerHelper.MineBlocks(6)`.

For LN-side payment tests, use `DockerHelper.CreateLndInvoice(sats)` to mint an LND BOLT11 for the plugin to pay (submarine swap direction).

## CI implications

- Bump `e2e.yml` job timeout from 20 → 30 min. Boltz + chain swap tests each take 30-60s; the full suite is projected at 15-20 min wall time.
- No new docker services. `submodules/NNark/regtest/start-env.sh` already provides arkd, ark-wallet, boltz, boltz-fulmine, lnd, cln, bitcoin, nbxplorer, esplora.
- Postgres service container stays at port 5432 with `btcpay_e2e_test` DB; ServerTester creates the schema per run via `newDb: true`.

## Out of scope

- Coop refund / co-op-spend edge cases — covered by `NArk.Tests.End2End.Swaps` already.
- VTXO unroll / unilateral exit paths — separate spec.
- Multi-store / multi-user concurrency — separate spec.
- Performance / load tests — out of scope for functional e2e.
