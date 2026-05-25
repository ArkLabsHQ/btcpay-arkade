# Arkade Asset Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace single-asset acceptance with a managed list of tracked Arkade assets (add-by-id with indexer prefill, free-form per-asset rate script, currency registration) and surface asset holdings in the VTXO table and compact balance.

**Architecture:** Each store's payment-method config holds `List<TrackedArkadeAsset>`. A plugin `CurrencyDataProvider` registers each asset's code as a BTCPay currency (union across stores; `CurrencyNameTable.ReloadCurrencyData` after CRUD). `AssetRateResolver` evaluates the asset's free-form rate-rule script via `RateRules.Combine(assetRules, storeRules)` + `RateFetcher` — the global `StoreBlob.RateScript` is never mutated. Display views read `ArkVtxo.Assets` / `AssetMetadataService`.

**Tech Stack:** C# / ASP.NET Core (BTCPay plugin), Razor views, NUnit (`NArk.E2E.Tests`), BTCPay `RateRules`/`RateFetcher`/`CurrencyNameTable`.

**Branch:** `feat/arkade-assets-payment` (extends PR #55; ships as one PR). Spec: `docs/superpowers/specs/2026-05-22-arkade-asset-management-design.md`.

**Pre-req baseline:** `dotnet build BTCPayServer.Plugins.ArkPayServer/BTCPayServer.Plugins.ArkPayServer.csproj` is green; asset unit tests run via `dotnet test NArk.E2E.Tests --filter "FullyQualifiedName~AssetRateResolver|FullyQualifiedName~AssetAmount"`.

---

## File Structure

| File | Responsibility | Action |
|------|----------------|--------|
| `PaymentHandler/ArkadePaymentMethodConfig.cs` | `TrackedArkadeAsset` record + `TrackedAssets` list; drop `ArkadeAssetAcceptance`/`AssetRateMode` | Modify |
| `Services/AssetRateResolver.cs` | Evaluate free-form rate script → asset units | Modify |
| `Services/ArkadeAssetCurrencyDataProvider.cs` | Register tracked-asset codes as BTCPay currencies | Create |
| `Services/AssetCurrencyRegistrar.cs` | Trigger `CurrencyNameTable.ReloadCurrencyData` after CRUD | Create |
| `Models/TrackedAssetViewModels.cs` | List/add/edit view models + fetch-result DTO | Create |
| `Controllers/ArkController.cs` | CRUD command handlers + fetch-metadata endpoint | Modify |
| `Views/Ark/StoreOverview.cshtml` | Tracked-assets list + add/edit modal (replaces single-asset modal) | Modify |
| `Views/Ark/Vtxos.cshtml`, `Views/Shared/_VtxoTable.cshtml` | Asset badge under Amount | Modify |
| `Views/Shared/_ArkBalances.cshtml` | Asset section in compact branch | Modify |
| `ArkPlugin.cs` | Register `CurrencyDataProvider` + `AssetCurrencyRegistrar` | Modify |
| `NArk.E2E.Tests/AssetRateResolverTests.cs` | Rate-script eval tests | Modify |
| `NArk.E2E.Tests/TrackedArkadeAssetTests.cs` | Validation tests | Create |

---

## Task 1: TrackedArkadeAsset data model

**Files:**
- Modify: `BTCPayServer.Plugins.ArkPayServer/PaymentHandler/ArkadePaymentMethodConfig.cs`
- Create: `NArk.E2E.Tests/TrackedArkadeAssetTests.cs`

- [ ] **Step 1: Write failing validation tests**

Create `NArk.E2E.Tests/TrackedArkadeAssetTests.cs`:

```csharp
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using NUnit.Framework;

namespace NArk.E2E.Tests;

[TestFixture]
public class TrackedArkadeAssetTests
{
    private static TrackedArkadeAsset Make(string code = "USDARK", string script = "USDARK_USD = 1;") =>
        new(AssetId: "abc123", CurrencyCode: code, Ticker: "USDARK", Name: "USD Arkade",
            Decimals: 2, RateScript: script, Enabled: true);

    [Test]
    public void Valid_config_passes()
    {
        Assert.That(Make().IsValid(out var err), Is.True);
        Assert.That(err, Is.Null);
    }

    [Test]
    public void Missing_asset_id_fails()
    {
        var a = Make() with { AssetId = "" };
        Assert.That(a.IsValid(out var err), Is.False);
        Assert.That(err, Does.Contain("asset id"));
    }

    [Test]
    public void Missing_currency_code_fails()
    {
        Assert.That((Make() with { CurrencyCode = " " }).IsValid(out _), Is.False);
    }

    [Test]
    public void Empty_rate_script_fails()
    {
        Assert.That((Make() with { RateScript = "" }).IsValid(out _), Is.False);
    }

    [Test]
    public void Negative_decimals_fails()
    {
        Assert.That((Make() with { Decimals = -1 }).IsValid(out _), Is.False);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test NArk.E2E.Tests --filter "FullyQualifiedName~TrackedArkadeAssetTests"`
Expected: FAIL — `TrackedArkadeAsset` does not exist / no `TrackedAssets`.

- [ ] **Step 3: Replace the single-asset model with TrackedArkadeAsset**

Rewrite `ArkadePaymentMethodConfig.cs` — remove `AssetAcceptance` param, `AssetRateMode` enum, and `ArkadeAssetAcceptance` record; add `TrackedAssets` + `TrackedArkadeAsset`:

```csharp
namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public record ArkadePaymentMethodConfig(
    string WalletId,
    bool GeneratedByStore = false,
    bool AllowSubDustAmounts = false,
    bool BoardingEnabled = true,
    long MinBoardingAmountSats = ArkadePaymentMethodConfig.DefaultMinBoardingAmountSats,
    IReadOnlyList<TrackedArkadeAsset>? TrackedAssets = null)
{
    public const long P2trDustLimitSats = 330L;
    public const long DefaultMinBoardingAmountSats = 5000L;

    /// <summary>Tracked assets, never null (empty when none configured).</summary>
    public IReadOnlyList<TrackedArkadeAsset> Assets => TrackedAssets ?? [];
}

/// <summary>
/// A store-tracked Arkade asset the merchant accepts as payment. The rate is
/// merchant-declared via a free-form BTCPay rate-rule <see cref="RateScript"/>
/// (assets aren't exchange-listed). Ticker/Name/Decimals are cached from the
/// arkd indexer for display and settlement math.
/// </summary>
public record TrackedArkadeAsset(
    string AssetId,
    string CurrencyCode,
    string? Ticker,
    string? Name,
    int Decimals,
    string RateScript,
    bool Enabled)
{
    public bool IsValid(out string? error)
    {
        if (string.IsNullOrWhiteSpace(AssetId)) { error = "An asset id is required."; return false; }
        if (string.IsNullOrWhiteSpace(CurrencyCode)) { error = "A currency code is required."; return false; }
        if (Decimals is < 0 or > 18) { error = "Decimals must be between 0 and 18."; return false; }
        if (string.IsNullOrWhiteSpace(RateScript)) { error = "A rate script is required."; return false; }
        error = null;
        return true;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test NArk.E2E.Tests --filter "FullyQualifiedName~TrackedArkadeAssetTests"`
Expected: PASS (5 tests). Build of the plugin will currently fail elsewhere (callers of the removed `AssetAcceptance`) — that's fixed in Tasks 2/4/5; do not commit yet.

- [ ] **Step 5: Commit after Task 2 (the resolver) compiles** — the model + resolver are one coherent compile unit.

---

## Task 2: AssetRateResolver — evaluate free-form rate script

**Files:**
- Modify: `BTCPayServer.Plugins.ArkPayServer/Services/AssetRateResolver.cs`
- Modify: `NArk.E2E.Tests/AssetRateResolverTests.cs`

- [ ] **Step 1: Read the current resolver + its tests**

Read `Services/AssetRateResolver.cs` and `NArk.E2E.Tests/AssetRateResolverTests.cs` fully so the new tests mirror existing setup (how `StoreData`, `RateFetcher`, `DefaultRulesCollection` are constructed/mocked).

- [ ] **Step 2: Write failing test for script-driven resolution**

Replace the body of `AssetRateResolverTests.cs` tests to pass a `TrackedArkadeAsset` (with a `RateScript`) instead of `ArkadeAssetAcceptance`. Add a self-contained case (no external rate) — script pegs the asset directly to BTC:

```csharp
[Test]
public async Task SatsPeg_script_resolves_units_without_external_rate()
{
    // 1 unit = 1000 sats  ⇒  MYA_BTC = 100000 (units per BTC, since 1 BTC = 1e8 sats / 1000)
    var asset = new TrackedArkadeAsset("id", "MYA", "MYA", "My Asset",
        Decimals: 0, RateScript: "MYA_BTC = 100000;", Enabled: true);
    var store = BuildStore();           // existing test helper
    var resolver = new AssetRateResolver(BuildRateFetcher(), DefaultRules());

    var due = await resolver.ResolveAsync(store, asset, dueSats: 50_000, CancellationToken.None);

    // 50_000 sats = 0.0005 BTC; 0.0005 * 100000 = 50 units
    Assert.That(due.DisplayUnits, Is.EqualTo(50m));
    Assert.That(due.BaseUnits, Is.EqualTo(50UL));
}
```

(Keep/adjust existing money-math cases — round-up, min-one-base-unit — passing a `TrackedArkadeAsset`. Decimals now come from `asset.Decimals`.)

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test NArk.E2E.Tests --filter "FullyQualifiedName~AssetRateResolverTests"`
Expected: FAIL — `ResolveAsync` signature still takes `ArkadeAssetAcceptance`.

- [ ] **Step 4: Rewrite `ResolveAsync` to compile + evaluate the script**

Replace the `switch` over `AssetRateMode` with rate-script evaluation. New signature drops `acceptance`/`assetDecimals`, takes the asset:

```csharp
using BTCPayServer.Rating;          // RateRules, CurrencyPair

public async Task<AssetAmountDue> ResolveAsync(
    StoreData store, TrackedArkadeAsset asset, long dueSats, CancellationToken cancellationToken)
{
    if (!asset.IsValid(out var configError))
        throw new InvalidOperationException($"Invalid tracked asset: {configError}");
    if (dueSats <= 0)
        throw new InvalidOperationException("Invoice amount due must be positive to price an asset.");

    if (!RateRules.TryParse(asset.RateScript, out var assetRules, out var parseErrors))
        throw new InvalidOperationException(
            $"Invalid rate script for {asset.CurrencyCode}: {string.Join("; ", parseErrors)}");

    // Combine the asset's rule with the store's existing rules so chained legs
    // (e.g. MYA_USD plus the store's BTC_USD) resolve. Store RateScript is NOT mutated.
    var storeRules = store.GetStoreBlob().GetRateRules(defaultRules);
    var combined = RateRules.Combine([assetRules, storeRules]);

    var pair = new CurrencyPair("BTC", asset.CurrencyCode);   // units of asset per 1 BTC
    var rule = combined.GetRuleFor(pair);
    var rate = await rateFetcher.FetchRate(rule, new StoreIdRateContext(store.Id), cancellationToken);
    if (rate.BidAsk is null || rate.Errors is { Count: > 0 })
        throw new InvalidOperationException(
            $"Unable to evaluate rate for {pair}" +
            (rate.Errors is { Count: > 0 } ? $" ({string.Join(", ", rate.Errors)})" : ""));

    var dueBtc = dueSats / 100_000_000m;
    var unitsPerBtc = rate.BidAsk.Center;
    var displayUnits = dueBtc * unitsPerBtc;
    var rateDescription = $"{pair} = {unitsPerBtc}; {dueBtc} BTC = {displayUnits} {asset.CurrencyCode}";

    // base-unit math (unchanged): round UP, clamp to >= 1 base unit
    var scale = AssetAmount.Pow10(asset.Decimals);
    var baseUnitsExact = Math.Max(1m, Math.Ceiling(displayUnits * scale));
    var baseUnits = (ulong)baseUnitsExact;
    return new AssetAmountDue(baseUnits, baseUnitsExact / scale,
        AssetAmount.Format(baseUnits, asset.Decimals), rateDescription);
}
```

Update the `AssetRateResolver` class doc comment to describe the script model (drop the `AssetRateMode` prose).

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test NArk.E2E.Tests --filter "FullyQualifiedName~AssetRateResolverTests"`
Expected: PASS.

- [ ] **Step 6: Remove the asset block from the existing Arkade (BTC-VTXO) handler**

In `PaymentHandler/ArkadePaymentMethodHandler.cs`, **delete** the `if (arkadePaymentMethodConfig.AssetAcceptance is { } acceptance) { … }` block (≈ lines 116–156, ending before `context.Prompt.Details = JObject.FromObject(details, Serializer);`) and any now-unused `AssetId/AssetName/AssetTicker/AssetDecimals/AssetBaseUnitsDue/AssetFormattedAmountDue` writes on the BTC prompt details. Asset pricing now lives entirely on the new `ARKADE-ASSET` method (Task 10). The BTC-VTXO Arkade prompt becomes asset-free again. `AssetRateResolver` is now called only by the new handler (Task 10), so it compiles but is unreferenced until then — that's expected.

- [ ] **Step 7: Build (resolver + model compile; old asset callers removed)**

Run: `dotnet build BTCPayServer.Plugins.ArkPayServer/BTCPayServer.Plugins.ArkPayServer.csproj -clp:ErrorsOnly`
Expected: 0 errors (the StoreOverview view/controller asset-acceptance references are removed in Tasks 5–6; if building before those, temporarily comment them — but prefer doing Tasks 1–6 before this build).

- [ ] **Step 8: Commit Tasks 1+2**

```bash
git add BTCPayServer.Plugins.ArkPayServer/PaymentHandler/ArkadePaymentMethodConfig.cs \
        BTCPayServer.Plugins.ArkPayServer/PaymentHandler/ArkadePaymentMethodHandler.cs \
        BTCPayServer.Plugins.ArkPayServer/Services/AssetRateResolver.cs \
        NArk.E2E.Tests/TrackedArkadeAssetTests.cs NArk.E2E.Tests/AssetRateResolverTests.cs
git commit -m "feat(assets): tracked-asset model + free-form rate-script resolver"
```

---

## Task 3: Currency registration

**Files:**
- Create: `BTCPayServer.Plugins.ArkPayServer/Services/ArkadeAssetCurrencyDataProvider.cs`
- Create: `BTCPayServer.Plugins.ArkPayServer/Services/AssetCurrencyRegistrar.cs`
- Modify: `BTCPayServer.Plugins.ArkPayServer/ArkPlugin.cs`

- [ ] **Step 1: Implement the CurrencyDataProvider**

`ArkadeAssetCurrencyDataProvider.cs` — reads every store's Arkade config (via `store.GetPaymentMethodConfig<ArkadePaymentMethodConfig>(...)`, the same accessor `ArkController` uses) and exposes each tracked asset's code as a `CurrencyData`:

```csharp
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Rating;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkadeAssetCurrencyDataProvider(
    StoreRepository stores,
    PaymentMethodHandlerDictionary handlers) : CurrencyDataProvider
{
    public async Task<CurrencyData[]> LoadCurrencyData(CancellationToken cancellationToken)
    {
        var seen = new Dictionary<string, CurrencyData>(StringComparer.OrdinalIgnoreCase);
        foreach (var store in await stores.GetStores())
        {
            var cfg = store.GetPaymentMethodConfig<ArkadePaymentMethodConfig>(
                ArkadePlugin.ArkadePaymentMethodId, handlers);
            if (cfg is null) continue;
            foreach (var a in cfg.Assets)
            {
                if (string.IsNullOrWhiteSpace(a.CurrencyCode)) continue;
                seen.TryAdd(a.CurrencyCode, new CurrencyData   // first-wins on cross-store collision
                {
                    Code = a.CurrencyCode,
                    Name = a.Name ?? a.Ticker ?? a.CurrencyCode,
                    Divisibility = a.Decimals,
                    Symbol = a.Ticker ?? a.CurrencyCode,
                    Crypto = true,
                });
            }
        }
        return seen.Values.ToArray();
    }
}
```

- [ ] **Step 2: Implement the reload helper**

`AssetCurrencyRegistrar.cs`:

```csharp
using BTCPayServer.Rating;
namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>Refreshes BTCPay's currency table after a tracked-asset CRUD op so a
/// newly-added asset code is recognised without a process restart.</summary>
public class AssetCurrencyRegistrar(CurrencyNameTable currencies)
{
    public Task RefreshAsync(CancellationToken ct = default) => currencies.ReloadCurrencyData(ct);
}
```

- [ ] **Step 3: Register in DI**

In `ArkPlugin.cs` `RegisterPluginServices` (near the other `AddSingleton`s):

```csharp
services.AddSingleton<BTCPayServer.Rating.CurrencyDataProvider, Services.ArkadeAssetCurrencyDataProvider>();
services.AddSingleton<Services.AssetCurrencyRegistrar>();
```

- [ ] **Step 4: Build**

Run: `dotnet build BTCPayServer.Plugins.ArkPayServer/BTCPayServer.Plugins.ArkPayServer.csproj -clp:ErrorsOnly`
Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Services/ArkadeAssetCurrencyDataProvider.cs \
        BTCPayServer.Plugins.ArkPayServer/Services/AssetCurrencyRegistrar.cs \
        BTCPayServer.Plugins.ArkPayServer/ArkPlugin.cs
git commit -m "feat(assets): register tracked assets as BTCPay currencies"
```

---

## Task 4: Metadata fetch endpoint (add-by-id prefill)

**Files:**
- Modify: `BTCPayServer.Plugins.ArkPayServer/Controllers/ArkController.cs`
- Create: `BTCPayServer.Plugins.ArkPayServer/Models/TrackedAssetViewModels.cs` (DTO)

- [ ] **Step 1: Define the fetch-result DTO**

In `Models/TrackedAssetViewModels.cs`:

```csharp
namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class AssetMetadataResult
{
    public bool Found { get; set; }
    public string AssetId { get; set; } = "";
    public string? Ticker { get; set; }
    public string? Name { get; set; }
    public int Decimals { get; set; }
}
```

- [ ] **Step 2: Add the controller endpoint**

In `ArkController.cs` (inject `AssetMetadataService` if not already), add an authorized JSON action:

```csharp
[HttpGet("stores/{storeId}/ark/asset-metadata")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public async Task<IActionResult> FetchAssetMetadata(string storeId, string assetId, CancellationToken ct)
{
    assetId = assetId?.Trim() ?? "";
    if (string.IsNullOrEmpty(assetId)) return Json(new AssetMetadataResult { Found = false });
    var details = await assetMetadataService.GetAssetDetailsAsync(assetId, ct);
    if (details is null) return Json(new AssetMetadataResult { Found = false, AssetId = assetId });
    return Json(new AssetMetadataResult
    {
        Found = true,
        AssetId = assetId,
        Ticker = assetMetadataService.GetTicker(details),
        Name = assetMetadataService.GetName(details),
        Decimals = assetMetadataService.GetDecimals(details),
    });
}
```

- [ ] **Step 3: Build**

Run: `dotnet build BTCPayServer.Plugins.ArkPayServer/BTCPayServer.Plugins.ArkPayServer.csproj -clp:ErrorsOnly`
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Controllers/ArkController.cs \
        BTCPayServer.Plugins.ArkPayServer/Models/TrackedAssetViewModels.cs
git commit -m "feat(assets): asset-metadata fetch endpoint for add-by-id prefill"
```

---

## Task 5: CRUD command handlers + view models

**Files:**
- Modify: `BTCPayServer.Plugins.ArkPayServer/Models/TrackedAssetViewModels.cs`
- Modify: `BTCPayServer.Plugins.ArkPayServer/Models/StoreOverviewViewModel.cs`
- Modify: `BTCPayServer.Plugins.ArkPayServer/Controllers/ArkController.cs`

- [ ] **Step 1: Add view models**

Append to `Models/TrackedAssetViewModels.cs`:

```csharp
public class TrackedAssetRow
{
    public string AssetId { get; set; } = "";
    public string CurrencyCode { get; set; } = "";
    public string? Ticker { get; set; }
    public string? Name { get; set; }
    public int Decimals { get; set; }
    public string RateScript { get; set; } = "";
    public bool Enabled { get; set; }
}
```

Add to `StoreOverviewViewModel`: `public List<TrackedAssetRow> TrackedAssets { get; set; } = [];` (replacing the `AssetAcceptance*` scalar fields), and populate it in the GET action from `config.Assets`.

- [ ] **Step 2: Replace `save-asset-acceptance` with CRUD commands**

In the `StoreOverview` POST handler, replace the `command == "save-asset-acceptance"` branch with `add-asset` / `edit-asset` / `remove-asset` (the model carries one `TrackedAssetRow` for add/edit and an `assetId` for remove). Each: load config, mutate `TrackedAssets`, validate via `TrackedArkadeAsset.IsValid`, persist, then `await assetCurrencyRegistrar.RefreshAsync()`. Example add:

```csharp
if (command == "add-asset")
{
    var row = model.AssetForm; // bound TrackedAssetRow
    var asset = new TrackedArkadeAsset(row.AssetId.Trim(), row.CurrencyCode.Trim().ToUpperInvariant(),
        row.Ticker, row.Name, row.Decimals, row.RateScript.Trim(), row.Enabled);
    if (!asset.IsValid(out var err))
        return RedirectWithError(nameof(StoreOverview), err!, new { storeId });
    var list = config.Assets.ToList();
    if (list.Any(a => a.CurrencyCode.Equals(asset.CurrencyCode, StringComparison.OrdinalIgnoreCase)))
        return RedirectWithError(nameof(StoreOverview), $"Currency code {asset.CurrencyCode} already tracked.", new { storeId });
    // Verify the asset exists on the indexer (parity with the old save-asset-acceptance check).
    if (await assetMetadataService.GetAssetDetailsAsync(asset.AssetId, HttpContext.RequestAborted) is null)
        return RedirectWithError(nameof(StoreOverview),
            $"Asset '{asset.AssetId}' not found on the Arkade indexer.", new { storeId });
    list.Add(asset);
    var newConfig = config! with { TrackedAssets = list };
    store!.SetPaymentMethodConfig(paymentMethodHandlerDictionary[ArkadePlugin.ArkadePaymentMethodId], newConfig);
    await storeRepository.UpdateStore(store);
    await assetCurrencyRegistrar.RefreshAsync(HttpContext.RequestAborted);
    return RedirectWithSuccess(nameof(StoreOverview), $"Asset {asset.CurrencyCode} added.", new { storeId });
}
```

(`edit-asset`: replace the matching `AssetId` in the list; `remove-asset`: filter it out. Both persist via the same `SetPaymentMethodConfig` + `storeRepository.UpdateStore(store)` pair shown above — identical to the old `save-asset-acceptance` path — then `await assetCurrencyRegistrar.RefreshAsync(...)`.)

- [ ] **Step 3: Build**

Run: `dotnet build BTCPayServer.Plugins.ArkPayServer/BTCPayServer.Plugins.ArkPayServer.csproj -clp:ErrorsOnly`
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Models/ BTCPayServer.Plugins.ArkPayServer/Controllers/ArkController.cs
git commit -m "feat(assets): tracked-asset CRUD command handlers + view models"
```

---

## Task 6: CRUD UI (StoreOverview)

**Files:**
- Modify: `BTCPayServer.Plugins.ArkPayServer/Views/Ark/StoreOverview.cshtml`

- [ ] **Step 1: Replace the single-asset modal with a tracked-assets list + add/edit modal**

Replace the `Asset Payments` row (around line 152) and `#assetAcceptanceModal` (around line 496) with:
- A **table** of `Model.TrackedAssets` (Code · Ticker/Name · Decimals · Enabled · rate-script preview · Edit/Remove buttons), each row posting `remove-asset` / opening the edit modal prefilled.
- An **add/edit modal** with: Asset ID input + a **Fetch** button (JS `fetch('stores/{storeId}/ark/asset-metadata?assetId=...')` → fills Ticker/Name/Decimals/suggested Code), Currency Code, Decimals, Rate Script textarea, Enabled checkbox; posts `add-asset`/`edit-asset`.

Mirror the existing modal markup/anti-forgery/`asp-for` conventions in this same file (the old `#assetAcceptanceModal`, lines ~496–540) for styling and form wiring.

- [ ] **Step 2: Build + visual check**

Run: `dotnet build BTCPayServer.Plugins.ArkPayServer/BTCPayServer.Plugins.ArkPayServer.csproj -clp:ErrorsOnly`
Expected: 0 errors. (Manual UI verification deferred to the run-through at Task 9.)

- [ ] **Step 3: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Views/Ark/StoreOverview.cshtml
git commit -m "feat(assets): tracked-asset CRUD UI with add-by-id fetch"
```

---

## Task 7: VTXO asset badge

**Files:**
- Modify: `BTCPayServer.Plugins.ArkPayServer/Views/Ark/Vtxos.cshtml`
- Modify: `BTCPayServer.Plugins.ArkPayServer/Views/Shared/_VtxoTable.cshtml`

- [ ] **Step 1: Render asset holdings under the Amount cell**

In both views, where the Amount cell renders (`Vtxos.cshtml` line ~186; `_VtxoTable.cshtml` line ~105), append for asset-carrying VTXOs:

```razor
@if (vtxo.Assets is { Count: > 0 })
{
    foreach (var asset in vtxo.Assets)
    {
        var d = await AssetMetadata.GetAssetDetailsAsync(asset.AssetId);
        var label = AssetMetadata.GetTicker(d) ?? (asset.AssetId.Length > 10 ? asset.AssetId[..10] + "…" : asset.AssetId);
        <span class="badge text-bg-success" style="font-size:.7rem;" data-sensitive>
            @AssetMetadata.FormatAmount(asset.Amount, d) @label
        </span>
    }
}
```

Inject `@inject BTCPayServer.Plugins.ArkPayServer.Services.AssetMetadataService AssetMetadata` at the top of each view. (`_VtxoTable.cshtml` already does async indexer calls in `@{ }`, so `await` in the view is fine; match its existing pattern.)

- [ ] **Step 2: Build**

Run: `dotnet build BTCPayServer.Plugins.ArkPayServer/BTCPayServer.Plugins.ArkPayServer.csproj -clp:ErrorsOnly`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Views/Ark/Vtxos.cshtml BTCPayServer.Plugins.ArkPayServer/Views/Shared/_VtxoTable.cshtml
git commit -m "feat(assets): show asset holdings on asset-carrying VTXOs"
```

---

## Task 8: Compact balance assets

**Files:**
- Modify: `BTCPayServer.Plugins.ArkPayServer/Views/Shared/_ArkBalances.cshtml`

- [ ] **Step 1: Add an asset section to the compact branch**

In the `if (compactMode)` block (lines ~17–53), after the BTC balance items, mirror the full-mode asset section (lines ~161–183) in compact form:

```razor
@if (Model.AssetBalances.Count > 0)
{
    foreach (var asset in Model.AssetBalances)
    {
        <div class="d-flex align-items-center gap-2">
            <span class="text-muted small">@(asset.Ticker ?? asset.Name ?? "Asset"):</span>
            <span class="fw-semibold text-success" data-testid="asset-balance" data-sensitive>
                @asset.FormattedAmount@(string.IsNullOrEmpty(asset.Ticker) ? "" : " " + asset.Ticker)
            </span>
        </div>
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build BTCPayServer.Plugins.ArkPayServer/BTCPayServer.Plugins.ArkPayServer.csproj -clp:ErrorsOnly`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Views/Shared/_ArkBalances.cshtml
git commit -m "feat(assets): show asset balances in compact balance (dashboard)"
```

---

## Task 9: Part 1 checkpoint (foundation + display)

- [ ] **Step 1:** `dotnet build BTCPayServer.Plugins.ArkPayServer/BTCPayServer.Plugins.ArkPayServer.csproj -clp:ErrorsOnly` → 0 errors.
- [ ] **Step 2:** `dotnet test NArk.E2E.Tests --filter "FullyQualifiedName~AssetRateResolver|FullyQualifiedName~AssetAmount|FullyQualifiedName~TrackedArkadeAsset"` → PASS.
- [ ] **Step 3:** Confirm Tasks 1–8 are committed (foundation + currency + CRUD + display). Part 1 is now self-contained.

---

# Part 2 — Arkade Asset payment method

> Mirror the existing **`ARKADE`** method's files for all BTCPay scaffolding (registration, handler `ConfigurePrompt`, link extension, checkout component) — they are the concrete template. Each task names the exact file to mirror. Spikes (explicitly marked) are bounded "read the ARKADE equivalent + confirm" steps for the integration points the spec flagged as risks.

## Task 10: `ARKADE-ASSET` payment method scaffolding

**Files:**
- Create: `PaymentHandler/ArkadeAssetPromptDetails.cs`
- Create: `PaymentHandler/ArkadeAssetPaymentMethodHandler.cs`
- Modify: `ArkPlugin.cs`

- [ ] **Step 1: Prompt details type**

`ArkadeAssetPromptDetails.cs`:

```csharp
namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public record ArkadeAssetOption(
    string AssetId, string CurrencyCode, string? Ticker, int Decimals,
    ulong BaseUnitsDue, string FormattedDue, string Bip321Uri);

public class ArkadeAssetPromptDetails
{
    public string ArkAddress { get; set; } = "";
    public List<ArkadeAssetOption> Options { get; set; } = [];
}
```

- [ ] **Step 2: Register the method (mirror `ArkPlugin.cs` lines 40–98 / 274–275)**

```csharp
internal const string AssetCheckoutBodyComponentName = "arkadeAssetCheckoutBody";
internal static readonly PaymentMethodId ArkadeAssetPaymentMethodId = new("ARKADE-ASSET");
// in RegisterPluginServices, beside the ARKADE registrations:
services.AddSingleton<ArkadeAssetPaymentMethodHandler>();
services.AddSingleton<IPaymentMethodHandler>(sp => sp.GetRequiredService<ArkadeAssetPaymentMethodHandler>());
services.AddSingleton<ArkadeAssetPaymentLinkExtension>();       // Task 12
services.AddSingleton<IPaymentLinkExtension>(sp => sp.GetRequiredService<ArkadeAssetPaymentLinkExtension>());
services.AddDefaultPrettyName(ArkadeAssetPaymentMethodId, "Arkade Asset");
services.AddSingleton<ArkadeAssetCheckoutModelExtension>();     // Task 12
services.AddSingleton<ICheckoutModelExtension>(sp => sp.GetRequiredService<ArkadeAssetCheckoutModelExtension>());
```

- [ ] **Step 3: Handler — read `PaymentHandler/ArkadePaymentMethodHandler.cs` fully, then mirror its `ConfigurePrompt`**

The asset handler's `ConfigurePrompt` reuses the **same Ark receive address derivation** the BTC handler uses (Spike: copy that exact address path — do not reinvent it), then:

```csharp
var cfg = context.StorePaymentMethodConfig<ArkadePaymentMethodConfig>(); // mirror how ARKADE reads its config
var dueSats = Money.Coins(context.Prompt.Calculate().Due).Satoshi;
var options = new List<ArkadeAssetOption>();
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
foreach (var a in cfg.Assets.Where(a => a.Enabled))
{
    AssetAmountDue due;
    try { due = await assetRateResolver.ResolveAsync(context.Store, a, dueSats, cts.Token); }
    catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
    { context.Logs.Write($"Asset {a.CurrencyCode} unavailable: {ex.Message}", InvoiceEventData.EventSeverity.Warning); continue; }
    var uri = ArkadeBip21Builder.Create().WithArkAddress(arkAddress)
        .WithAsset(a.AssetId, due.BaseUnits).Build();      // Task 11
    options.Add(new ArkadeAssetOption(a.AssetId, a.CurrencyCode, a.Ticker, a.Decimals,
        due.BaseUnits, due.FormattedAmount, uri));
}
if (options.Count == 0)
    throw new PaymentMethodUnavailableException("No Arkade asset is available for this invoice.");
context.Prompt.Destination = arkAddress;
context.Prompt.Details = JObject.FromObject(new ArkadeAssetPromptDetails { ArkAddress = arkAddress, Options = options }, Serializer);
```

(`ParsePaymentPromptDetails`/`ParsePaymentMethodConfig`/`GetPaymentLinkExtension` etc.: mirror the ARKADE handler's interface members verbatim, swapping the details type.)

- [ ] **Step 4:** Build → 0 errors. Commit `feat(assets): ARKADE-ASSET payment method + prompt`.

## Task 11: BIP-321 asset URI

**Files:** Modify the Arkade BIP-21 builder (`Services/ArkadeBip21Builder.cs` — confirm path); Test: `NArk.E2E.Tests/ArkadeBip21AssetTests.cs` (create).

- [ ] **Step 1: Failing test**

```csharp
[Test]
public void WithAsset_appends_asset_id_and_amount()
{
    var uri = ArkadeBip21Builder.Create().WithArkAddress("ark1q...").WithAsset("deadbeef", 150).Build();
    Assert.That(uri, Does.Contain("asset=deadbeef"));   // key confirmed in Step 3
}
```

- [ ] **Step 2:** Run → FAIL (no `WithAsset`).
- [ ] **Step 3: Implement `WithAsset(string assetId, ulong baseUnitsDue)`** on the builder, appending the asset id (+ amount) to the URI query. **Spike:** confirm the exact param key against the Arkade ts-sdk / wallet convention — `grep -ri "asset" submodules/NNark` and the ts-sdk fixtures under `NArk.Tests/Assets/Fixtures`; if no canonical key exists, use `asset` and note it in the test. Wire `baseUnitsDue` into the existing amount param the builder already emits.
- [ ] **Step 4:** Run → PASS. Commit.

## Task 12: Multi-asset checkout (payer picks)

**Files:**
- Create: `PaymentHandler/ArkadeAssetPaymentLinkExtension.cs` (mirror `ArkadePaymentLinkExtension.cs`)
- Create: `PaymentHandler/ArkadeAssetCheckoutModelExtension.cs` (mirror `ArkadeCheckoutModelExtension.cs`)
- Create: checkout component view for `arkadeAssetCheckoutBody` (mirror `Views/Shared/Arkade/ArkadeMethodCheckout.cshtml` + its view-component registration)

- [ ] **Step 1: Link extension** — `GetPaymentLink` returns the first option's `Bip321Uri` from the parsed `ArkadeAssetPromptDetails`. Mirror `ArkadePaymentLinkExtension` structure.
- [ ] **Step 2: Checkout model extension** — set `context.Model.CheckoutBodyComponentName = ArkadePlugin.AssetCheckoutBodyComponentName` and expose `Options` to the component. **Spike:** read `ArkadeCheckoutModelExtension.ModifyCheckoutModel` + the `arkadeCheckoutBody` component to confirm how the body component receives its data (`context.Model.AdditionalData[...]` vs a typed model) and mirror that exactly.
- [ ] **Step 3: Checkout component** — render a selectable list of options; each option shows ticker/name + `FormattedDue` + a `<vc:qr-code data="@option.Bip321Uri" />` and a copy field. Mirror the markup/conventions in `ArkadeMethodCheckout.cshtml`.
- [ ] **Step 4:** Build → 0 errors. Commit `feat(assets): multi-asset Arkade Asset checkout`.

## Task 13: Settlement wiring

**Files:** Modify `Services/ArkContractInvoiceListener.cs`.

- [ ] **Step 1:** The listener already credits asset arrivals proportionally (`vtxo.Assets`). Extend `OnVtxoChanged` so that when the matched invoice has an `ARKADE-ASSET` prompt whose `Options` contains an arriving asset id, it registers/settles the **`ARKADE-ASSET`** payment (reuse `HandlePaymentData` with the BTC-equivalent amount override, `isBoarding: false`). Match strictly by asset id against `ArkadeAssetPromptDetails.Options[].AssetId`; ignore assets not offered by the invoice. Leave the BTC-VTXO `ARKADE` settlement path untouched.
- [ ] **Step 2:** Build → 0 errors. Commit `feat(assets): settle Arkade Asset payments on matching asset arrival`.

## Task 14: Final verification + PR

- [ ] **Step 1:** Full build → 0 errors.
- [ ] **Step 2:** `dotnet test NArk.E2E.Tests --filter "FullyQualifiedName~AssetRateResolver|FullyQualifiedName~AssetAmount|FullyQualifiedName~TrackedArkadeAsset|FullyQualifiedName~ArkadeBip21Asset"` → PASS.
- [ ] **Step 3:** Extend `NArk.E2E.Tests/AssetAcceptanceTests.cs`: add-by-id → list → edit → remove; and an asset-checkout + settlement happy path (create invoice → `ARKADE-ASSET` offers the asset → pay the asset → invoice settles).
- [ ] **Step 4: Manual checklist (record in PR):** add asset by id (prefill + currency registered); the Arkade Asset checkout shows one option per enabled asset with per-asset QR/amount; paying the chosen asset settles the invoice; VTXO table shows the asset badge; dashboard (compact) shows the asset balance.
- [ ] **Step 5:** Update CHANGELOG; push; refresh PR #55 description; iterate CI to green.

---

## Notes / risks (from spec)

- **Rate-rule compile/merge:** uses `RateRules.TryParse` + `RateRules.Combine([assetRules, storeRules])` + `GetRuleFor` + `RateFetcher.FetchRate` — verified APIs; no `StoreBlob.RateScript` mutation.
- **Currency reload:** `CurrencyNameTable.ReloadCurrencyData` after CRUD; the format-provider cache repopulates lazily, so late-added codes fall back to default formatting (acceptable).
- **Code collisions:** unique within a store (validated); cross-store first-wins in the provider (logged), acceptable because rate eval uses the store's own asset record.
