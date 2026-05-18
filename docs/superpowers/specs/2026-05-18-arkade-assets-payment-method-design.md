# Arkade Assets as a BTCPay payment method — design, parity & decisions

**Date:** 2026-05-18
**Mode:** autonomous overnight session (no user blocking)
**Builds on:** PR #25 `quick-beacon` (`docs/plans/2026-02-15-arkade-assets-receive-send.md`)
**Status:** living doc — research in progress, decisions recorded as made

## Goal (from user)

1. Audit Arkade asset support across go-sdk / rust-sdk / ts-sdk; bring NNark (.NET SDK) to **full feature + test parity**.
2. Update the BTCPay plugin assets PR (#25, `quick-beacon`) and bring git up to date.
3. Let merchants **configure a store to accept an Arkade asset as payment**.
4. Let merchants **specify a rate** for that asset (integrate with BTCPay's rate system).
5. Full UX: store config, checkout, UI — everything. Tests at parity with the SDKs.

## Ground truth — NNark current asset surface (verified locally)

- **Operations:** `IAssetManager` = `IssueAsync`, `ReissueAsync`, `BurnAsync` (`AssetManager.cs`). Transfer/send is via `SpendingService` + `AssetRequirement` coin selection (not on `IAssetManager`). Receive = VTXO sync maps `VtxoAsset`.
- **Controlled issuance/reissuance:** supported via `AssetRef.FromId` (arkd verifies control asset exists).
- **Serialization stack:** `AssetId`, `AssetRef`, `AssetGroup`, `AssetInput`, `AssetOutput`, `AssetMetadata`, `MetadataList`, `Packet`, `AssetPacketBuilder`, `BufferReader/Writer`, `Extension` (`NArk.Core/Assets/`).
- **Transport:** `GrpcClientTransport.Assets.cs` / `RestClientTransport.Assets.cs` → `GetAssetDetailsAsync` (indexer `GetAsset`). `ArkAssetDetails` model.
- **Unit tests (~129 methods, NArk.Tests/Assets/):** AssetGroup 14, AssetId 11, AssetInputOutput 24, AssetRef 10, Buffer 10, Extension 9, Fixture 4, Metadata 16, Packet 16; +AssetPacketBuilder 7, +MergeAssets 8. Shared JSON fixtures: `asset_group_fixtures.json`, `asset_id_fixtures.json`, `extension_fixtures.json`, `packet_fixtures.json`.
- **E2E tests (NArk.Tests.End2End/AssetTests.cs, 7 ordered):** CanIssueAsset, CanTransferAssetBetweenWallets, CanBurnAsset, AssetsSurviveBatchSettlement, CanIssueAssetWithControlAsset, CanReissueAssetWithControlAsset, CanIssueAssetWithMetadata.
- **NNark submodule:** local `master` @ `a89d47a`, **behind** origin/master (upstream has more, incl. swaps fixes). Parity work must target current upstream → sync required.

## SDK parity matrix (PENDING — 3 research agents running)

go-sdk / ts-sdk / rust-sdk asset inventories are being gathered by parallel
`ark-researcher` agents (deepwiki + GitHub). go-sdk `pkg/ark-lib/asset/` is
the canonical byte-layout reference per PR #25. Fill on agent return:

| Capability | go-sdk | ts-sdk | rust-sdk | NNark | Gap/action |
|---|---|---|---|---|---|
| _(matrix filled when agents report)_ | | | | | |

Then: close every NNark feature gap, and match their test scenarios
(reusing shared JSON fixture vectors where they exist).

## BTCPay plugin — PR #25 current state

`quick-beacon` (4 commits, ~1981 ins): asset UI (balance display, VTXO
badges, send wizard, metadata cache), `AssetsJson` VTXO column + migration,
`AssetMetadataService`, design+plan docs. **Scope was receive+send+display
only.** It does NOT cover accept-as-payment, rates, or checkout — that is
the new scope this session adds.

Stale ~3 months; base `master` moved a lot (deadlock fix, e2e harness,
etc.). Needs rebase onto current master.

## Decision: asset-as-payment-method + rate model

**Problem:** Arkade assets are arbitrary tokens, not on Kraken/Coinbase, so
BTCPay's exchange rate providers can't price them. But BTCPay's rate
*scripting* supports constant & derived expressions and composing with
fiat (`FOO_BTC = FOO_USD * USD_BTC`).

**Decision (made autonomously; rationale recorded):** add per-asset rate
config to the Arkade store settings. Each accepted asset declares its price
as **either**:
- (a) a fixed price in a reference currency (e.g. `1 unit = 0.01 USD`) —
  plugin converts invoice amount → asset units using BTCPay's existing
  fiat rate pipeline for the reference leg, then the configured asset
  price; **or**
- (b) a direct sats-per-unit price (no external rate needed).

This leverages BTCPay's `RateRules`/currency engine for the fiat leg
(spread, fallback, existing invoice rate flow all keep working) instead of
bypassing it, while keeping the asset-specific price merchant-controlled.
Stablecoin-style assets use (a) with their pegged currency.

**Why not pure rate-script pseudo-currency:** would require the merchant to
hand-author rate rules and invent currency codes; assets have no exchange
feed so the rule is always a constant anyway — config UI is clearer and
less error-prone for the same result, while still routing the fiat leg
through BTCPay rates.

## Implementation plan (sequenced, each step independently committable)

1. **Sync NNark** submodule to upstream master; branch `assets-parity`.
2. **NNark parity**: close feature gaps from the matrix; add tests
   mirroring sibling-SDK scenarios; reuse shared fixtures. Unit + e2e.
   Keep `dotnet test NArk.Tests` green; gate e2e on infra.
3. **Plugin rebase**: `quick-beacon` onto current `master`; resolve
   conflicts; build green.
4. **Accept-as-payment**: store-scoped Arkade-asset payment method config
   (which asset id(s), rate model a/b, decimals from metadata).
5. **Rate integration**: invoice creation resolves asset price via the
   chosen model; reference-currency leg through BTCPay rates.
6. **Checkout**: asset payment method tab — amount in asset units, address
   = asset-carrying Arkade address, QR/BIP21 with asset params, paid
   detection via asset-aware VTXO sync.
7. **UI**: store settings page for asset acceptance + rate; checkout
   rendering; balance/VTXO badges already in #25.
8. **Tests**: plugin e2e for configure-asset-acceptance, invoice-in-asset,
   pay-asset-invoice → settled. Iterate CI green.

## Constraints honored

- No stubs/placeholders/TODOs; no skipped tests; branch off master; iterate
  CI green; mention commit hashes; no Co-Authored-By; "Arkade"/"batch"
  vocabulary in NNark user-facing strings; update NNark README/docs/sample
  on public API changes (NNark CLAUDE.md).
