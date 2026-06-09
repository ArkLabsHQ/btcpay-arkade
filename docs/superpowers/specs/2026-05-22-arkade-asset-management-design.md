# Arkade asset management + asset payment method — design

**Date:** 2026-05-22
**Branch:** `feat/arkade-assets-payment` (extends PR #55; ships as one PR)
**Status:** approved design, pre-implementation

## Problem

The asset-acceptance feature (PR #55, unmerged) lets a store accept **one** Arkade asset, configured by hand (asset id + a fixed `FixedReferenceCurrency`/`SatsPerUnit` enum + price), priced onto the existing **Arkade (BTC-VTXO)** payment prompt. Gaps:

1. **Adding an asset is manual** — nothing is fetched from the indexer.
2. **Rates are constrained to two hard-coded models**, and the asset isn't a first-class BTCPay currency.
3. **Only one asset per store**, no management.
4. **No real multi-asset checkout** — the single asset is bolted onto the BTC prompt; there's no payer-facing choice and no asset-aware payment URI.

Plus two display gaps: asset-carrying VTXOs show only their dust sat amount; the **compact** balance panel (dashboard + widget) omits asset balances the full panel shows.

## Goals

- A **managed list of tracked assets** (CRUD), each added **by id with indexer prefill** (ticker/name/decimals).
- Each asset carries a **free-form BTCPay rate-rule script**, evaluated via the store's rate engine (never mutating the global rate script).
- Register each tracked asset as a **BTCPay currency** (code/decimals/symbol).
- A **dedicated "Arkade Asset" payment method** with a **multi-asset checkout**: one option per enabled asset, each a **BIP-321 URI carrying the Ark address + asset id + amount due**; the payer picks which asset to pay in.
- Surface asset holdings in the **VTXO table** and **compact balance**.

## Non-goals

- Asset issuance/minting UI (SDK already supports issuance).
- Auto-discovering rates externally — assets aren't exchange-listed; rates stay merchant-declared.
- Mutating the store's global `StoreBlob.RateScript`.
- Dynamic per-asset BTCPay `PaymentMethodId`s (BTCPay methods are static registrations) — one fixed `ARKADE-ASSET` method hosts all assets.

## Decisions (resolved during brainstorming)

- **Rate mechanism:** register the asset as a currency *and* keep the rate rule plugin-owned — evaluate via `RateFetcher` against the store's rules; don't edit `StoreBlob.RateScript`.
- **Rate input:** **free-form** BTCPay rate-rule script per asset (e.g. `USDARK_USD = 1;`).
- **Multi-asset settlement:** **payer picks at checkout**, surfaced through **one dedicated `ARKADE-ASSET` payment method** (not per-asset methods; not on the BTC prompt). Each enabled asset is a selectable option with its own BIP-321 (Ark address + asset id) URI + amount due.
- **VTXO display:** inline badge under the Amount cell.
- **Scope:** foundation + asset payment method + display, all in one spec/plan/PR.
- **No migration:** asset acceptance was never shipped (releases v2.1.15–v2.1.18 exclude it), so the single `AssetAcceptance` is removed outright.

## Part 1 — Asset foundation

### A. Data model

Replace the single nullable `ArkadeAssetAcceptance` on `ArkadePaymentMethodConfig` with:

```csharp
public record ArkadePaymentMethodConfig(
    ..., IReadOnlyList<TrackedArkadeAsset>? TrackedAssets = null)
{
    public IReadOnlyList<TrackedArkadeAsset> Assets => TrackedAssets ?? [];
}

public record TrackedArkadeAsset(
    string AssetId, string CurrencyCode, string? Ticker, string? Name,
    int Decimals, string RateScript, bool Enabled);   // + IsValid(out error)
```

- Stored in the existing per-store payment-method config blob — no new table.
- The `AssetRateMode` enum and `ArkadeAssetAcceptance` record are deleted.

### B. CRUD flow (Arkade store settings)

A **"Tracked assets"** section (table) with Add / Edit / Remove. **Add:** type asset id → **Fetch** (`AssetMetadataService.GetAssetDetailsAsync`) prefills ticker/name/decimals + suggests a `CurrencyCode` → merchant enters the rate-rule script → save (validate id exists on indexer + code unique within store + script compiles). On every CRUD op, reload the currency table (C).

### C. Currency registration

A plugin `ArkadeAssetCurrencyDataProvider : CurrencyDataProvider` exposes the union of all stores' tracked-asset codes as BTCPay currencies (`Code`, `Divisibility = Decimals`, `Symbol = Ticker`, `Crypto = true`); `AssetCurrencyRegistrar` calls `CurrencyNameTable.ReloadCurrencyData()` after CRUD. Cross-store code collisions are first-wins (logged); within-store uniqueness is validated.

### D. Rate resolution

`AssetRateResolver` compiles the asset's free-form `RateScript` (`RateRules.TryParse`), `RateRules.Combine([assetRules, storeRules])`, `GetRuleFor(new CurrencyPair("BTC", asset.CurrencyCode))`, and `RateFetcher.FetchRate` → units-per-BTC → asset units due. Settlement math (round-up, min one base unit) unchanged. A missing/uncompilable/unfetchable rate makes that asset simply not be offered (never a hard failure).

## Part 2 — Arkade Asset payment method

### E. The `ARKADE-ASSET` payment method

A second, fixed payment method registered alongside `ARKADE`, mirroring its wiring in `ArkPlugin.cs`:

- `ArkadePlugin.ArkadeAssetPaymentMethodId = new("ARKADE-ASSET")`; `AddDefaultPrettyName(…, "Arkade Asset")`.
- `ArkadeAssetPaymentMethodHandler : IPaymentMethodHandler` — configures the prompt: the **same Ark receive address** as the BTC Arkade method (assets settle to the same VTXO script), plus per-asset amounts. Prompt details carry the Ark address + a list of `{ AssetId, CurrencyCode, Ticker, Decimals, BaseUnitsDue, FormattedDue, Bip321Uri }` for each **enabled** tracked asset (priced via D). If no enabled asset prices successfully, the method is unavailable (`PaymentMethodUnavailableException`).
- `ArkadeAssetPaymentLinkExtension : IPaymentLinkExtension` — default link = the first/selected asset's BIP-321 URI.
- Checkout component `arkadeAssetCheckoutBody` + `ArkadeAssetCheckoutModelExtension : ICheckoutModelExtension`.
- **Availability:** the method is auto-enabled for a store when the wallet is configured (same lifecycle as `ARKADE`); at prompt time it's shown only if ≥1 enabled asset prices successfully.

### F. Multi-asset checkout (payer picks)

The `arkadeAssetCheckoutBody` component renders **one selectable option per enabled asset** (ticker/name + amount due + QR/URI). Selecting an asset shows its BIP-321 URI and the amount in that asset's units. All options settle to the same Ark address; the difference is which asset the payer sends. (No payer state is persisted server-side — settlement is detected by which asset actually arrives; see H.)

### G. BIP-321 asset URI

Extend `ArkadeBip21Builder` with `WithAsset(string assetId, ulong baseUnitsDue)` producing the unified URI carrying the **Ark address + `asset=<assetId>` + amount** (param shape per the Arkade BIP-321/ts-sdk convention — confirm exact key during implementation). BTC/lightning params are omitted for the asset options (an asset option is asset-only).

### H. Settlement

The invoice listener already credits **asset arrivals** (`vtxo.Assets`) proportionally to the BTC amount due. Extend it to also register/settle the **`ARKADE-ASSET`** payment when the arriving asset matches one of the invoice's offered assets (match by asset id against the prompt's per-asset list), crediting the BTC-equivalent for accounting. The existing BTC-VTXO Arkade settlement path is untouched.

## Part 3 — Display

### I. VTXO table + compact balance

- **VTXO table** (`Vtxos.cshtml`, `_VtxoTable.cshtml`): inline badge under Amount per `vtxo.Assets` entry, `formattedAmount TICKER` (via `AssetMetadataService`).
- **Compact `_ArkBalances.cshtml`:** mirror the full-mode "Arkade assets" section into the compact branch.

## Components & boundaries

| Unit | Responsibility |
|------|----------------|
| `TrackedArkadeAsset`, `ArkadePaymentMethodConfig.TrackedAssets` | Per-asset config + list |
| `AssetMetadataService` (exists) | Indexer fetch + cache (prefill + display) |
| `ArkadeAssetCurrencyDataProvider` / `AssetCurrencyRegistrar` (new) | Currency registration + reload |
| `AssetRateResolver` (generalized) | Compile + evaluate per-asset rate script |
| Arkade store-settings controller/views | Tracked-asset CRUD + fetch endpoint |
| `ArkadeAssetPaymentMethodHandler` + prompt details (new) | `ARKADE-ASSET` prompt: Ark address + per-asset amounts/URIs |
| `ArkadeAssetCheckoutModelExtension` + `arkadeAssetCheckoutBody` (new) | Multi-asset picker checkout |
| `ArkadeAssetPaymentLinkExtension` + `ArkadeBip21Builder.WithAsset` (new/extended) | Per-asset BIP-321 URI |
| `ArkContractInvoiceListener` (extended) | Settle `ARKADE-ASSET` on matching asset arrival |
| `Vtxos.cshtml` / `_VtxoTable.cshtml` / `_ArkBalances.cshtml` | Display surfacing |

## Data flow (asset payment)

1. Merchant tracks asset(s) (B) → registered as currencies (C).
2. Invoice created → `ARKADE-ASSET` handler prices each enabled asset (D) → prompt holds Ark address + per-asset amounts + BIP-321 URIs (E/G).
3. Checkout shows the asset picker (F); payer sends the chosen asset to the Ark address.
4. Asset VTXO arrives → listener matches asset id → settles the `ARKADE-ASSET` payment (H).

## Testing

- **Unit (NArk.E2E.Tests, runnable):** `AssetRateResolver` script compilation + money math; `TrackedArkadeAsset` validation; `ArkadeBip21Builder.WithAsset` URI shape.
- **E2E:** extend `AssetAcceptanceTests` to cover add-by-id/list/edit/remove; an asset-checkout + settlement happy path.
- **Display/UI:** build verification + manual checkout/dashboard/VTXO-table check.

## Risks / validate during implementation

1. **Rate-rule compile/merge** — `RateRules.TryParse` + `Combine` + `GetRuleFor` + `RateFetcher` (verified APIs); no `StoreBlob` mutation.
2. **CurrencyNameTable reload** — `ReloadCurrencyData` after CRUD; format-provider cache repopulates lazily (late codes fall back to default formatting; acceptable).
3. **Payment-method availability/enablement** — confirm how `ARKADE-ASSET` is auto-enabled per store (mirror `ARKADE`'s lifecycle) and hidden when no asset prices.
4. **Checkout component data flow** — confirm how `arkadeAssetCheckoutBody` receives the per-asset model (mirror `arkadeCheckoutBody`).
5. **BIP-321 asset param key** — confirm the exact `asset=` convention against the Arkade ts-sdk / wallet expectations.
6. **Cross-store currency-code collisions** — first-wins (logged); revisit if problematic.
