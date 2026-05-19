# Arkade Assets as a BTCPay payment method — design, parity & decisions

**Date:** 2026-05-18
**Mode:** autonomous overnight session (no user blocking)
**Builds on:** PR #25 `quick-beacon` (`docs/plans/2026-02-15-arkade-assets-receive-send.md`)
**Status:** implemented — see "Implemented" section at end

## Implemented (plugin PR #55, NNark PR #94)

Branch `feat/arkade-assets-payment` off current `master`; supersedes #25
(closed). NNark submodule pinned at `bbcd960` — the squash-merge of
PR #94 on NNark `master` (the `assets-parity` branch / its pre-squash
commits `8c0fe77`+`5509d8a` were orphaned by the squash and deleted;
pinning the durable master commit keeps fresh CI clones fetchable).

| Slice | Commit(s) | What |
|---|---|---|
| NNark GAP B+C | NNark `bbcd960` (squash of PR #94, merged to master) | deterministic group ordering + ts-sdk fixtures; 393/393; README determinism note |
| Balances | `54e3858` | per-asset spendable balances on dashboard (`AssetMetadataService`) |
| Config | `37cd63a` | `ArkadeAssetAcceptance` (additive, serialization-safe) + `IsValid` |
| Rate resolver | `66bcd2b`, `71a2f5a` | `AssetRateResolver` (SatsPerUnit self-contained; FixedReferenceCurrency via store `RateFetcher`); round-up never-underpay; 11 unit tests; fixed a "100"→"1" format bug |
| Settings UI | `ca31f1e` | overview row + modal; save/disable commands; indexer existence check |
| Checkout | `b1370fc` | prompt resolves asset due; "Pay {amount} {ticker}" notice |
| Settlement | `1894e4f` | listener settles asset invoice on asset arrival (BTC credited ∝ asset received, capped 100%); stray BTC VTXO ignored |
| E2E | `82a4f42` | config modal + server-validation round-trip tests |

**Not built (decided):** GAP A (mint-new-control-asset-same-tx) — ts-sdk
canonical is id-only; rust-only convenience, arkd-unverifiable. Full
pay-with-asset settlement e2e (needs issued-asset funding infra) —
money math is unit-tested instead.

---
_original research notes below_

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

## SDK parity matrix (findings)

**Format parity: PROVEN.** `dotnet test NArk.Tests --filter Assets` → **131/131
pass**. NNark's `FixtureTests`/`ExtensionTests` are driven by JSON vectors
sourced from `arkade-os/ts-sdk/test/fixtures/`; identical hex for every
shared vector = byte-level parity with ts-sdk/go-sdk (all SDKs implement one
wire spec).

**rust-sdk (`arkade-os/rust-sdk`, ark-core/ark-client) vs NNark:**

| Capability | rust-sdk | NNark | Verdict |
|---|---|---|---|
| Magic/packet-type/presence bits | ✓ ARK/0x00/0x01-04 | ✓ identical | parity |
| Fresh issuance | ✓ | ✓ `IssueAsync` | parity |
| Controlled issuance — existing ctrl by id | ✓ `ControlAssetConfig::existing` | ✓ `IssuanceParams.ControlAssetId` | parity |
| Controlled issuance — **mint NEW ctrl same-tx** | ✓ `ControlAssetConfig::New{amount}` | serialization supports `AssetRef.FromGroupIndex`, but **not exposed on `IAssetManager.IssueAsync`** | **GAP A** |
| Reissuance | ✓ | ✓ `ReissueAsync` | parity |
| Burn | ✓ | ✓ `BurnAsync` | parity |
| Transfer/send | ✓ | ✓ SpendingService + `ArkTxOut.Assets` | parity |
| Asset coin selection + change | ✓ | ✓ `AssetRequirement` | parity |
| Batch settlement preservation | ✓ | ✓ (e2e `AssetsSurviveBatchSettlement`) | parity |
| Deterministic group ordering in send packet | ✓ sorts by (txid,groupIndex) | **verify** | **GAP B?** |
| Indexer GetAsset gRPC | ✓ | ✓ | parity |
| Indexer GetAsset **REST** | ✗ absent | ✓ `RestClientTransport.Assets` | NNark ahead |
| Metadata wire decode (hex binary) | ✗ raw string | ✓ `MetadataList.FromString` | NNark ahead |
| Shared JSON fixture vectors | ✗ inline only | ✓ ts-sdk fixtures | NNark ahead |
| BIP-341 taptree over groups | ✗ not found | ✓ PR #16 | NNark ahead |
| `LeafTxPacket` intent conversion | ✗ not found | ✓ | NNark ahead |

**ts-sdk:** agent still running (canonical fixture source; refs ts-sdk#279,
arkd#814). NNark already passes its fixtures → format parity holds; await
agent for any higher-level API/test scenarios to mirror.

**ts-sdk (canonical, `@arkade-os/sdk` v0.4.27, fixture source) vs NNark — final verdict:**

- Format/ops parity: **proven** (131/131; NNark passes ts-sdk shared vectors).
- ts-sdk `AssetManager.issue()` is **also id-only** (no same-tx new-control)
  → **GAP A DROPPED**: NNark matches the canonical SDK; rust's
  `ControlAssetConfig::New` is a rust-only convenience, not the cross-SDK
  contract. Adding an arkd-unverifiable public API "for parity" would
  violate it. Documented, intentionally not implemented.
- ts-sdk has **zero asset e2e tests**; NNark has **7** → NNark far ahead.
- **GAP B (real, fix):** NNark `AssetPacketBuilder.Build` orders groups via
  `HashSet<string>` → non-deterministic. rust-sdk sorts by
  (txid,groupIndex). Deterministic packets are correct regardless
  (reproducibility, fixture stability). → sort groups by AssetId + test.
- **GAP C (real, fix — directly answers "same level of tests"):** ts-sdk
  ships 4 cross-SDK fixture files NNark's dir lacks:
  `asset_ref_fixtures.json`, `asset_input_fixtures.json`,
  `asset_output_fixtures.json`, `metadata_fixtures.json`. NNark has the
  types + hand-written tests but not the shared vectors. → import the 4
  files, add fixture-driven tests consuming them (mirrors ts-sdk
  `test/asset.test.ts`), align error strings to fixture-expected wording.

**Net:** NNark meets/exceeds canonical (ts-sdk) asset parity. Bounded
actions: GAP B + GAP C (both unit-testable, no infra). Then plugin.

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
