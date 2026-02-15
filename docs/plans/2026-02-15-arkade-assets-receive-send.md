# Arkade Assets — Receive + Send

**Date:** 2026-02-15
**Scope:** Receive and send Arkade Assets (no issuance/burn)
**References:**
- [arkade-os/ts-sdk#279](https://github.com/arkade-os/ts-sdk/pull/279) — TypeScript SDK asset support
- [arkade-os/arkd#814](https://github.com/arkade-os/arkd/pull/814) — Server-side asset offchain flow

## Overview

Arkade Assets are token-like assets that ride on top of Ark VTXOs. Asset data is encoded as an OP_RETURN output (a "packet") in off-chain Ark transactions. Each VTXO can carry zero or more assets alongside its BTC value.

This design adds asset awareness across the full NNark + plugin stack: data model, transport, coin selection, spending, storage, and UI. The initial scope is receive + send only (no issuance, reissuance, or burn).

## Architecture

Changes span two repos worked in parallel:
- **NNark submodule** (dotnet-sdk) — data model, encoding library, transport, coin selector, spending service
- **BTCPayServer plugin** (ArkPayServer) — storage, UI, indexer integration

## Section 1: NNark Data Model Changes

### ArkVtxo — add assets field

```csharp
// New record in NArk.Abstractions.VTXOs
public record VtxoAsset(string AssetId, ulong Amount);
```

`ArkVtxo` record gets a new optional parameter:

```csharp
public record ArkVtxo(
    ...existing params...,
    IReadOnlyList<VtxoAsset>? Assets = null);
```

### ArkCoin — assets passthrough

`ArkCoin` gains an `Assets` property so spending logic has access to asset data from selected coins:

```csharp
public IReadOnlyList<VtxoAsset>? Assets { get; }
```

### Proto — types.proto

Match arkd#814 changes:

```protobuf
message Vtxo {
  // ...existing fields 1-13...
  repeated Asset assets = 14;
}

message Asset {
  string asset_id = 1;
  uint64 amount = 2;
}
```

### Transport — GrpcClientTransport.Vtxo.cs

Map the new proto `assets` field into `ArkVtxo.Assets`:

```csharp
Assets: vtxo.Assets.Count > 0
    ? vtxo.Assets.Select(a => new VtxoAsset(a.AssetId, a.Amount)).ToList()
    : null
```

## Section 2: NNark Asset Encoding Library

New namespace porting Go's `pkg/ark-lib/asset/` to C#.

### Types

| Type | Description |
|------|-------------|
| `AssetId` | 34-byte value: 32-byte txid + uint16 group index. Hex serializable. |
| `AssetRef` | Discriminated union: `ByID(AssetId)` or `ByGroup(uint16 index)` |
| `Metadata` | Key/value byte pair, varint-length prefixed serialization |
| `AssetInput` | Discriminated: `Local(vin, amount)` or `Intent(txid, vin, amount)` |
| `AssetOutput` | `(vout, amount)` |
| `AssetGroup` | `AssetId?`, `AssetRef?` (control), inputs, outputs, metadata. Presence bitmask. |
| `Packet` | Collection of `AssetGroup`s → OP_RETURN with `ARK` magic (0x41 0x52 0x4b) + `0x00` marker |

### Serialization

Binary format using `BufferReader`/`BufferWriter` helpers. Must produce the exact same byte layout as Go and TypeScript implementations:
- Varint encoding for lengths and amounts
- Presence bitmask byte for AssetGroup optional fields (0x01=AssetId, 0x02=ControlAsset, 0x04=Metadata)
- OP_RETURN script wrapping via `OP_RETURN <data>`

### Validation rules

- Issuance groups (null AssetId) must have no inputs
- Only issuance groups can have a control asset
- No duplicate vins within an AssetInputs list
- No duplicate vouts within an AssetOutputs list
- Control asset group index must be within packet bounds

### Packet.TxOut()

Returns a `TxOut` with `amount=0` and the OP_RETURN script, ready to append to Ark transaction outputs.

## Section 3: Spending Service — Asset-Aware Send

### ICoinSelector extension

```csharp
public record AssetRequirement(string AssetId, ulong Amount);

public interface ICoinSelector
{
    // Existing BTC-only method (unchanged)
    IReadOnlyCollection<ArkCoin> SelectCoins(
        List<ArkCoin> availableCoins,
        Money targetAmount,
        Money dustThreshold,
        int currentSubDustOutputs);

    // New: select coins satisfying both BTC and asset requirements
    IReadOnlyCollection<ArkCoin> SelectCoins(
        List<ArkCoin> availableCoins,
        Money targetBtcAmount,
        IReadOnlyList<AssetRequirement> assetRequirements,
        Money dustThreshold,
        int currentSubDustOutputs);
}
```

### DefaultCoinSelector — new overload

1. For each asset requirement, select coins carrying that asset (greedy, smallest-amount-first)
2. Accumulate BTC from asset-selected coins
3. Fill remaining BTC need using existing greedy strategy on non-selected coins
4. Same subdust-avoidance strategies apply

### ArkTxOut — asset allocation

```csharp
public record ArkTxOutAsset(string AssetId, ulong Amount);

public class ArkTxOut(...existing...)
{
    public IReadOnlyList<ArkTxOutAsset>? Assets { get; init; }
}
```

### SpendingService.Spend() changes

1. **Detect asset requirements** from outputs
2. **Call asset-aware coin selector** when outputs carry assets
3. **Compute asset change** — input assets minus output assets per asset ID. Assign asset change to the BTC change output.
4. **Construct Packet** — map inputs by coin index, outputs by tx output index, build asset groups, create Packet
5. **Append OP_RETURN** — `Packet.TxOut()` appended to outputs before passing to `ArkTransactionBuilder`
6. **OP_RETURN budget** — asset packet OP_RETURN counts against `MaxOpReturnOutputs` (currently 1). Asset packet takes priority when assets are present; subdust change uses the regular approach.

## Section 4: Plugin Storage & Database

### VTXO entity

Add a JSON column (same pattern as `CommitmentTxids`):

```csharp
public string? AssetsJson { get; set; }  // JSON: List<{AssetId, Amount}>
```

### EfCoreVtxoStorage

- `UpsertVtxo`: maps `ArkVtxo.Assets` → `AssetsJson`
- Read path: deserializes back
- No filtering by asset ID needed initially (load by script/wallet, filter in memory)

### Migration

One new migration adding nullable `AssetsJson` text column to the VTXO table.

### No separate asset table

The server's indexer tracks asset supply/metadata. The plugin only needs to know which VTXOs carry which assets for balance display and coin selection.

## Section 5: Plugin UI Changes

### Balance display

`ArkBalancesViewModel` / dashboard widgets show asset balances alongside BTC:
- Each asset: truncated asset ID, amount, metadata (name/ticker) when available
- Amount formatting uses `decimals` metadata field (default 8)

### VTXOs list

VTXOs carrying assets show an asset badge/tag next to the BTC amount. No separate page — same VTXOs with extra data.

### Send wizard

- Optional asset selector when wallet holds assets
- Recipients can specify `assetId` + `amount` alongside BTC amount
- `suggest-coins` and `validate-spend` APIs become asset-aware, passing asset requirements through to the spending service

### Receive page

No changes. Assets arrive on VTXOs at existing contract scripts. The sync service picks them up automatically.

## Section 6: Indexer Integration — GetAssetDetails

### NNark transport

New method on `IClientTransport`:

```csharp
Task<ArkAssetDetails> GetAssetDetails(string assetId, CancellationToken cancellationToken = default);
```

New model:

```csharp
public record ArkAssetDetails(
    string AssetId,
    ulong Supply,
    string? ControlAssetId,
    IReadOnlyDictionary<string, string>? Metadata);
```

### Proto — indexer.proto

Add from arkd#814:

```protobuf
rpc GetAsset(GetAssetRequest) returns (GetAssetResponse) {
  option (meshapi.gateway.http) = { get: "/v1/indexer/asset/{asset_id}" };
}

message GetAssetRequest { string asset_id = 1; }
message GetAssetResponse {
  string asset_id = 1;
  string supply = 2;
  repeated AssetMetadata metadata = 3;
  string control_asset = 4;
}
message AssetMetadata { string key = 1; string value = 2; }
```

### Plugin — asset metadata caching

In-memory cache keyed by asset ID. Asset metadata is immutable after issuance. Avoids repeated indexer calls on page loads.

## Summary

| Layer | Change | Size |
|-------|--------|------|
| NNark Proto | `assets` on Vtxo, `Asset` message, `GetAsset` RPC | Small |
| NNark Abstractions | `VtxoAsset`, `ArkVtxo.Assets`, `ArkCoin.Assets`, `ArkTxOut.Assets`, `ArkAssetDetails`, `AssetRequirement` | Small |
| NNark Asset Library | `AssetId`, `AssetRef`, `Metadata`, `AssetInput`, `AssetOutput`, `AssetGroup`, `Packet` + binary serialization | Medium |
| NNark Transport | Map proto assets → model, implement `GetAssetDetails` | Small |
| NNark Coin Selector | Asset-aware `ICoinSelector.SelectCoins` overload | Medium |
| NNark Spending Service | Asset change tracking, packet construction, OP_RETURN budget | Medium |
| Plugin Storage | `AssetsJson` column + migration | Small |
| Plugin UI | Balance display, VTXO badges, send wizard asset selector | Medium |
