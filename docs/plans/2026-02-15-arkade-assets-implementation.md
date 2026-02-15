# Arkade Assets (Receive + Send) Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add Arkade Asset awareness (receive + send, no issuance/burn) across NNark and the BTCPayServer plugin.

**Architecture:** Assets ride on top of VTXOs as OP_RETURN-encoded packets. Changes propagate bottom-up: proto → model → transport → coin selector → spending → storage → UI.

**Tech Stack:** C# / .NET 8, NBitcoin, gRPC/Protobuf, EF Core (PostgreSQL), NUnit + NSubstitute, Razor views.

**Repos:**
- NNark submodule: `submodules/NNark/` (from main repo root `C:\Git\NArk\`)
- Plugin: `BTCPayServer.Plugins.ArkPayServer/` (in worktree `C:\Git\NArk\.dev\worktree\quick-beacon\`)

---

## Task 1: Proto — Add Asset Messages

**Files:**
- Modify: `submodules/NNark/NArk.Core/Transport/GrpcClient/Protos/ark/v1/types.proto`
- Modify: `submodules/NNark/NArk.Core/Transport/GrpcClient/Protos/ark/v1/indexer.proto`

**Step 1: Add Asset message and assets field to Vtxo in types.proto**

After line 31 (`string ark_txid = 13;`) in the `Vtxo` message, add:

```protobuf
  repeated Asset assets = 14;
```

After the closing `}` of Vtxo, add:

```protobuf
message Asset {
  string asset_id = 1;
  uint64 amount = 2;
}
```

**Step 2: Add IndexerAsset and assets field to IndexerVtxo in indexer.proto**

After line 253 (`string ark_txid = 13;`) in the `IndexerVtxo` message, add:

```protobuf
  repeated IndexerAsset assets = 14;
```

After the closing `}` of IndexerVtxo, add:

```protobuf
message IndexerAsset {
  string asset_id = 1;
  uint64 amount = 2;
}
```

**Step 3: Add GetAsset RPC and messages to indexer.proto**

In the `IndexerService` service definition, before the `GetBatchSweepTransactions` rpc, add:

```protobuf
  rpc GetAsset(GetAssetRequest) returns (GetAssetResponse) {
    option (meshapi.gateway.http) = {
      get: "/v1/indexer/asset/{asset_id}"
    };
  }
```

After `GetVirtualTxsResponse`, add:

```protobuf
message GetAssetRequest {
  string asset_id = 1;
}

message GetAssetResponse {
  string asset_id = 1;
  string supply = 2;
  repeated AssetMetadata metadata = 3;
  string control_asset = 4;
}

message AssetMetadata {
  string key = 1;
  string value = 2;
}
```

**Step 4: Build to verify proto compilation**

```bash
cd submodules/NNark && dotnet build NArk.Core/NArk.Core.csproj
```

Expected: Build succeeds. Protobuf codegen produces C# types for Asset, IndexerAsset, GetAssetRequest, GetAssetResponse, AssetMetadata.

**Step 5: Commit**

```bash
cd submodules/NNark && git add -A && git commit -m "proto: add asset messages to types.proto and indexer.proto"
```

---

## Task 2: NNark Model — VtxoAsset and ArkVtxo.Assets

**Files:**
- Create: `submodules/NNark/NArk.Abstractions/VTXOs/VtxoAsset.cs`
- Modify: `submodules/NNark/NArk.Abstractions/VTXOs/ArkVtxo.cs`

**Step 1: Create VtxoAsset record**

```csharp
namespace NArk.Abstractions.VTXOs;

public record VtxoAsset(string AssetId, ulong Amount);
```

**Step 2: Add Assets parameter to ArkVtxo**

In `ArkVtxo.cs`, add `IReadOnlyList<VtxoAsset>? Assets = null` as the last parameter of the record:

```csharp
public record ArkVtxo(
    string Script,
    string TransactionId,
    uint TransactionOutputIndex,
    ulong Amount,
    string? SpentByTransactionId,
    string? SettledByTransactionId,
    bool Swept,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    uint? ExpiresAtHeight,
    bool Preconfirmed = false,
    bool Unrolled = false,
    IReadOnlyList<string>? CommitmentTxids = null,
    string? ArkTxid = null,
    IReadOnlyList<VtxoAsset>? Assets = null)
```

The rest of the file (methods, body) stays the same.

**Step 3: Build to verify**

```bash
cd submodules/NNark && dotnet build NArk.Abstractions/NArk.Abstractions.csproj
```

Expected: Build succeeds.

**Step 4: Commit**

```bash
cd submodules/NNark && git add -A && git commit -m "feat: add VtxoAsset record and Assets field to ArkVtxo"
```

---

## Task 3: NNark Model — ArkCoin.Assets Passthrough

**Files:**
- Modify: `submodules/NNark/NArk.Abstractions/ArkCoin.cs`
- Modify: `submodules/NNark/NArk.Core/Transformers/PaymentContractTransformer.cs`
- Modify: `submodules/NNark/NArk.Core/Transformers/NoteContractTransformer.cs`
- Modify: `submodules/NNark/NArk.Core/Transformers/HashLockedContractTransformer.cs`

**Step 1: Add Assets property to ArkCoin**

In `ArkCoin.cs`, add to the constructor parameter list (after `bool swept`):

```csharp
IReadOnlyList<VtxoAsset>? assets = null
```

Add `using NArk.Abstractions.VTXOs;` at the top.

In the constructor body, add:

```csharp
Assets = assets;
```

Add public property:

```csharp
public IReadOnlyList<VtxoAsset>? Assets { get; }
```

Update the copy constructor to also pass `other.Assets`.

**Step 2: Pass vtxo.Assets in all IContractTransformer implementations**

In `PaymentContractTransformer.cs`, update the `Transform` method's `new ArkCoin(...)` call to pass `assets: vtxo.Assets` as the last argument.

Do the same in `NoteContractTransformer.cs` and `HashLockedContractTransformer.cs`.

**Step 3: Build to verify all callers compile**

```bash
cd submodules/NNark && dotnet build
```

Fix any remaining callers that construct ArkCoin (tests, other code). Since `assets` has a default value of `null`, existing callers that use positional arguments should still compile. Named-argument callers and copy constructors need updating.

**Step 4: Commit**

```bash
cd submodules/NNark && git add -A && git commit -m "feat: add Assets passthrough to ArkCoin"
```

---

## Task 4: NNark Transport — Map Proto Assets to ArkVtxo

**Files:**
- Modify: `submodules/NNark/NArk.Core/Transport/GrpcClient/GrpcClientTransport.Vtxo.cs`

**Step 1: Map assets in GetVtxoByScriptsAsSnapshot**

In the `yield return new ArkVtxo(...)` block (around line 43-58), add `Assets:` parameter after `ArkTxid:`:

```csharp
Assets: vtxo.Assets.Count > 0
    ? vtxo.Assets.Select(a => new VtxoAsset(a.AssetId, a.Amount)).ToList()
    : null
```

Add `using NArk.Abstractions.VTXOs;` if not already present (VtxoAsset is in that namespace).

**Step 2: Build and verify**

```bash
cd submodules/NNark && dotnet build NArk.Core/NArk.Core.csproj
```

Expected: Build succeeds.

**Step 3: Commit**

```bash
cd submodules/NNark && git add -A && git commit -m "feat: map proto assets to ArkVtxo in transport layer"
```

---

## Task 5: NNark Transport — GetAssetDetails

**Files:**
- Create: `submodules/NNark/NArk.Core/Transport/Models/ArkAssetDetails.cs`
- Modify: `submodules/NNark/NArk.Core/Transport/IClientTransport.cs`
- Modify: `submodules/NNark/NArk.Core/Transport/GrpcClient/GrpcClientTransport.cs`
- Modify: `submodules/NNark/NArk.Core/Transport/CachingClientTransport.cs`

**Step 1: Create ArkAssetDetails model**

```csharp
namespace NArk.Core.Transport.Models;

public record ArkAssetDetails(
    string AssetId,
    ulong Supply,
    string? ControlAssetId,
    IReadOnlyDictionary<string, string>? Metadata);
```

**Step 2: Add to IClientTransport**

```csharp
Task<ArkAssetDetails> GetAssetDetailsAsync(string assetId, CancellationToken cancellationToken = default);
```

**Step 3: Implement in GrpcClientTransport**

Create a new partial file `GrpcClientTransport.Assets.cs` or add to the main file:

```csharp
public async Task<ArkAssetDetails> GetAssetDetailsAsync(string assetId, CancellationToken cancellationToken = default)
{
    var request = new GetAssetRequest { AssetId = assetId };
    var response = await _indexerServiceClient.GetAssetAsync(request, cancellationToken: cancellationToken);

    Dictionary<string, string>? metadata = null;
    if (response.Metadata.Count > 0)
    {
        metadata = response.Metadata.ToDictionary(m => m.Key, m => m.Value);
    }

    return new ArkAssetDetails(
        AssetId: response.AssetId,
        Supply: ulong.TryParse(response.Supply, out var supply) ? supply : 0,
        ControlAssetId: string.IsNullOrEmpty(response.ControlAsset) ? null : response.ControlAsset,
        Metadata: metadata);
}
```

**Step 4: Implement passthrough in CachingClientTransport**

```csharp
public Task<ArkAssetDetails> GetAssetDetailsAsync(string assetId, CancellationToken cancellationToken = default)
    => _inner.GetAssetDetailsAsync(assetId, cancellationToken);
```

**Step 5: Build and verify**

```bash
cd submodules/NNark && dotnet build
```

**Step 6: Commit**

```bash
cd submodules/NNark && git add -A && git commit -m "feat: add GetAssetDetails to transport layer"
```

---

## Task 6: NNark Model — ArkTxOut.Assets

**Files:**
- Create: `submodules/NNark/NArk.Abstractions/ArkTxOutAsset.cs`
- Modify: `submodules/NNark/NArk.Abstractions/ArkTxOut.cs`

**Step 1: Create ArkTxOutAsset record**

```csharp
namespace NArk.Abstractions;

public record ArkTxOutAsset(string AssetId, ulong Amount);
```

**Step 2: Add Assets property to ArkTxOut**

```csharp
public class ArkTxOut(ArkTxOutType type, Money amount, IDestination dest) : TxOut(amount, dest)
{
    public ArkTxOutType Type { get; } = type;
    public IReadOnlyList<ArkTxOutAsset>? Assets { get; init; }
}
```

**Step 3: Build to verify**

```bash
cd submodules/NNark && dotnet build NArk.Abstractions/NArk.Abstractions.csproj
```

**Step 4: Commit**

```bash
cd submodules/NNark && git add -A && git commit -m "feat: add ArkTxOutAsset and Assets property to ArkTxOut"
```

---

## Task 7: NNark Asset Encoding — BufferReader/BufferWriter

**Files:**
- Create: `submodules/NNark/NArk.Core/Assets/BufferWriter.cs`
- Create: `submodules/NNark/NArk.Core/Assets/BufferReader.cs`

**Step 1: Implement BufferWriter**

Binary serialization helper matching the Go/TS varint encoding. Must support:
- `WriteByte(byte)`, `Write(byte[])`, `WriteUint16LE(ushort)`
- `WriteVarInt(ulong)` — Bitcoin-style varint (1/3/5/9 bytes)
- `WriteVarSlice(byte[])` — varint-length-prefixed byte slice
- `ToBytes()` — returns the written data

**Step 2: Implement BufferReader**

Binary deserialization helper. Must support:
- `ReadByte()`, `ReadSlice(int)`, `ReadUint16LE()`
- `ReadVarInt()` — reads Bitcoin-style varint
- `ReadVarSlice()` — reads varint-length-prefixed byte slice
- `Remaining` — bytes left to read

**Step 3: Write tests**

Create `submodules/NNark/NArk.Tests/Assets/BufferTests.cs`:

```csharp
[TestFixture]
public class BufferTests
{
    [Test]
    public void WriteThenRead_VarInt_RoundTrips()
    {
        foreach (var value in new ulong[] { 0, 1, 0xfc, 0xfd, 0xffff, 0x10000, 0xffffffff, 0x100000000 })
        {
            var writer = new BufferWriter();
            writer.WriteVarInt(value);
            var reader = new BufferReader(writer.ToBytes());
            Assert.That(reader.ReadVarInt(), Is.EqualTo(value));
            Assert.That(reader.Remaining, Is.EqualTo(0));
        }
    }

    [Test]
    public void WriteThenRead_VarSlice_RoundTrips()
    {
        var data = new byte[] { 0x01, 0x02, 0x03 };
        var writer = new BufferWriter();
        writer.WriteVarSlice(data);
        var reader = new BufferReader(writer.ToBytes());
        var result = reader.ReadVarSlice();
        Assert.That(result, Is.EqualTo(data));
    }

    [Test]
    public void WriteThenRead_Uint16LE_RoundTrips()
    {
        var writer = new BufferWriter();
        writer.WriteUint16LE(0x0102);
        var reader = new BufferReader(writer.ToBytes());
        Assert.That(reader.ReadUint16LE(), Is.EqualTo(0x0102));
    }
}
```

**Step 4: Run tests**

```bash
cd submodules/NNark && dotnet test NArk.Tests --filter "FullyQualifiedName~BufferTests"
```

**Step 5: Commit**

```bash
cd submodules/NNark && git add -A && git commit -m "feat: add BufferReader/BufferWriter for asset binary encoding"
```

---

## Task 8: NNark Asset Encoding — Core Types

**Files:**
- Create: `submodules/NNark/NArk.Core/Assets/AssetConstants.cs`
- Create: `submodules/NNark/NArk.Core/Assets/AssetId.cs`
- Create: `submodules/NNark/NArk.Core/Assets/AssetRef.cs`
- Create: `submodules/NNark/NArk.Core/Assets/Metadata.cs`
- Create: `submodules/NNark/NArk.Core/Assets/AssetInput.cs`
- Create: `submodules/NNark/NArk.Core/Assets/AssetOutput.cs`

Port these types from the Go `pkg/ark-lib/asset/` and TypeScript `src/asset/` implementations. Each type must:
- Serialize/deserialize to the exact same binary format
- Validate the same constraints
- Support `FromBytes(byte[])`, `FromReader(BufferReader)`, `Serialize()`, `SerializeTo(BufferWriter)`, `ToString()` (hex)

**AssetConstants.cs:**
```csharp
namespace NArk.Core.Assets;

public static class AssetConstants
{
    public const int TxHashSize = 32;
    public const int AssetIdSize = 34; // 32 + 2
    public const byte AssetVersion = 0x01;
    public const byte MaskAssetId = 0x01;
    public const byte MaskControlAsset = 0x02;
    public const byte MaskMetadata = 0x04;
    public static readonly byte[] ArkadeMagic = { 0x41, 0x52, 0x4b }; // "ARK"
    public const byte MarkerAssetPayload = 0x00;
}

public enum AssetInputType : byte { Unspecified = 0, Local = 1, Intent = 2 }
public enum AssetRefType : byte { Unspecified = 0, ByID = 1, ByGroup = 2 }
```

Implement each type following the TS/Go patterns. Use the test fixtures from the arkd PR at `pkg/ark-lib/asset/testdata/` for cross-implementation compatibility testing.

**Step: Write tests using fixtures from arkd**

Create `submodules/NNark/NArk.Tests/Assets/AssetIdTests.cs` etc., loading the JSON fixture files and verifying that `FromBytes` + `Serialize` round-trips produce the same hex strings as the Go/TS implementations.

Download fixtures from arkd PR and place in `submodules/NNark/NArk.Tests/Assets/testdata/`.

**Step: Run tests**

```bash
cd submodules/NNark && dotnet test NArk.Tests --filter "FullyQualifiedName~Assets"
```

**Step: Commit**

```bash
cd submodules/NNark && git add -A && git commit -m "feat: add asset encoding core types (AssetId, AssetRef, Metadata, AssetInput, AssetOutput)"
```

---

## Task 9: NNark Asset Encoding — AssetGroup and Packet

**Files:**
- Create: `submodules/NNark/NArk.Core/Assets/AssetGroup.cs`
- Create: `submodules/NNark/NArk.Core/Assets/Packet.cs`

**Step 1: Implement AssetGroup**

Port from TS `src/asset/assetGroup.ts`. Key details:
- Presence bitmask byte: 0x01=AssetId, 0x02=ControlAsset, 0x04=Metadata
- Serialize: presence byte, optional fields, then varint-counted inputs, varint-counted outputs
- Validation: issuance (null AssetId) has no inputs; only issuance can have control asset

**Step 2: Implement Packet**

Port from TS `src/asset/packet.ts`. Key details:
- Serialize: varint group count, then each group, wrapped in `OP_RETURN <ARK_MAGIC><MARKER><data>`
- `TxOut()` method returns `new TxOut(Money.Zero, opReturnScript)`
- `IsAssetPacket(Script)` static method for detection
- Validation: groups non-empty, control asset group index in bounds

**Step 3: Write tests using fixtures**

Create `submodules/NNark/NArk.Tests/Assets/PacketTests.cs` using `packet_fixtures.json` and `asset_group_fixtures.json` from the arkd PR.

**Step 4: Run tests**

```bash
cd submodules/NNark && dotnet test NArk.Tests --filter "FullyQualifiedName~PacketTests"
```

**Step 5: Commit**

```bash
cd submodules/NNark && git add -A && git commit -m "feat: add AssetGroup and Packet encoding"
```

---

## Task 10: NNark Coin Selector — Asset-Aware Selection

**Files:**
- Create: `submodules/NNark/NArk.Core/CoinSelector/AssetRequirement.cs`
- Modify: `submodules/NNark/NArk.Core/CoinSelector/ICoinSelector.cs`
- Modify: `submodules/NNark/NArk.Core/CoinSelector/DefaultCoinSelector.cs`

**Step 1: Write failing tests first**

Add to `submodules/NNark/NArk.Tests/DefaultCoinSelectorTests.cs`:

```csharp
[Test]
public void SelectsCoinsWithAsset_WhenAssetRequired()
{
    // Coin A: 5000 sats, has AssetX=100
    // Coin B: 3000 sats, no assets
    // Target: 1000 sats BTC + AssetX=50
    var coins = new List<ArkCoin>
    {
        CreateCoinWithAssets(5000, [new VtxoAsset("asset_x", 100)]),
        CreateCoin(3000),
    };
    var requirements = new List<AssetRequirement> { new("asset_x", 50) };
    var result = _selector.SelectCoins(coins, Money.Satoshis(1000), requirements, Money.Satoshis(546), 0);

    // Should select coin A (has the asset) — its 5000 sats covers the 1000 BTC need too
    Assert.That(result, Has.Count.EqualTo(1));
    Assert.That(result.First().Assets, Is.Not.Null);
}

[Test]
public void SelectsAdditionalBtcCoins_WhenAssetCoinsInsufficientBtc()
{
    // Coin A: 500 sats, has AssetX=100
    // Coin B: 3000 sats, no assets
    // Target: 2000 sats BTC + AssetX=50
    var coins = new List<ArkCoin>
    {
        CreateCoinWithAssets(500, [new VtxoAsset("asset_x", 100)]),
        CreateCoin(3000),
    };
    var requirements = new List<AssetRequirement> { new("asset_x", 50) };
    var result = _selector.SelectCoins(coins, Money.Satoshis(2000), requirements, Money.Satoshis(546), 0);

    // Should select both: coin A for asset, coin B for remaining BTC
    Assert.That(result, Has.Count.EqualTo(2));
}

[Test]
public void ThrowsNotEnoughFunds_WhenAssetInsufficient()
{
    var coins = new List<ArkCoin>
    {
        CreateCoinWithAssets(5000, [new VtxoAsset("asset_x", 30)]),
        CreateCoin(3000),
    };
    var requirements = new List<AssetRequirement> { new("asset_x", 50) };

    Assert.Throws<NotEnoughFundsException>(() =>
        _selector.SelectCoins(coins, Money.Satoshis(1000), requirements, Money.Satoshis(546), 0));
}
```

Add helper:

```csharp
private static ArkCoin CreateCoinWithAssets(long satoshis, IReadOnlyList<VtxoAsset> assets)
{
    var key = new Key();
    var script = key.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
    var outpoint = new OutPoint(RandomUtils.GetUInt256(), 0);
    var txOut = new TxOut(Money.Satoshis(satoshis), script);

    var scriptBuilder = Substitute.For<NArk.Abstractions.Scripts.ScriptBuilder>();
    scriptBuilder.BuildScript().Returns(Enumerable.Empty<Op>());
    scriptBuilder.Build().Returns(new TapScript(Script.Empty, TapLeafVersion.C0));

    var contract = Substitute.For<ArkContract>(
        NArk.Abstractions.Extensions.KeyExtensions.ParseOutputDescriptor(
            "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88",
            Network.RegTest));

    return new ArkCoin(
        walletIdentifier: "test-wallet",
        contract: contract,
        birth: DateTimeOffset.UtcNow,
        expiresAt: null,
        expiresAtHeight: null,
        outPoint: outpoint,
        txOut: txOut,
        signerDescriptor: null,
        spendingScriptBuilder: scriptBuilder,
        spendingConditionWitness: null,
        lockTime: null,
        sequence: new Sequence(1),
        swept: false,
        assets: assets);
}
```

**Step 2: Run tests to verify they fail**

```bash
cd submodules/NNark && dotnet test NArk.Tests --filter "FullyQualifiedName~DefaultCoinSelectorTests"
```

Expected: New tests fail (method overload doesn't exist yet).

**Step 3: Create AssetRequirement**

```csharp
namespace NArk.Core.CoinSelector;

public record AssetRequirement(string AssetId, ulong Amount);
```

**Step 4: Add overload to ICoinSelector**

```csharp
IReadOnlyCollection<ArkCoin> SelectCoins(
    List<ArkCoin> availableCoins,
    Money targetBtcAmount,
    IReadOnlyList<AssetRequirement> assetRequirements,
    Money dustThreshold,
    int currentSubDustOutputs);
```

**Step 5: Implement in DefaultCoinSelector**

Algorithm:
1. For each asset requirement, filter coins that carry that asset. Sort by asset amount ascending (smallest first). Greedily select until requirement met. Track total BTC from selected coins.
2. Compute remaining BTC needed: `targetBtcAmount - btcFromAssetCoins`. If <= 0, done.
3. Otherwise, call the existing `SelectCoins` (BTC-only) with the remaining coins and remaining BTC target.
4. Merge the two sets.
5. Apply the same subdust-avoidance logic.

**Step 6: Run tests**

```bash
cd submodules/NNark && dotnet test NArk.Tests --filter "FullyQualifiedName~DefaultCoinSelectorTests"
```

Expected: All tests pass (old and new).

**Step 7: Commit**

```bash
cd submodules/NNark && git add -A && git commit -m "feat: add asset-aware coin selection"
```

---

## Task 11: NNark Spending Service — Asset Packet Construction

**Files:**
- Modify: `submodules/NNark/NArk.Core/Services/SpendingService.cs`

This is the most complex task. The `Spend` methods need to:

1. Detect if any output has `Assets`
2. If so, use the asset-aware coin selector overload
3. Compute asset change (sum input assets - sum output assets per asset ID)
4. Assign asset change to the BTC change output
5. Build a `Packet` from asset groups and append its `TxOut` to outputs
6. Handle the OP_RETURN budget (asset packet takes one slot)

**Step 1: Modify the `Spend(walletId, inputs, outputs)` overload**

After computing change and before calling `ConstructAndSubmitArkTransaction`, add asset packet construction logic:

```csharp
// Compute asset inputs (map from tx input index to assets)
var assetInputMap = new Dictionary<int, IReadOnlyList<VtxoAsset>>();
for (int i = 0; i < inputs.Length; i++)
{
    if (inputs[i].Assets is { Count: > 0 } assets)
        assetInputMap[i] = assets;
}

// Compute asset outputs
var assetOutputMap = new Dictionary<int, IReadOnlyList<ArkTxOutAsset>>();
for (int i = 0; i < outputs.Length; i++)
{
    if (outputs[i].Assets is { Count: > 0 } assets)
        assetOutputMap[i] = assets;
}

bool hasAssets = assetInputMap.Count > 0 || assetOutputMap.Count > 0;

if (hasAssets)
{
    // Compute asset change
    var inputAssetTotals = new Dictionary<string, ulong>();
    foreach (var (_, assets) in assetInputMap)
        foreach (var a in assets)
            inputAssetTotals[a.AssetId] = inputAssetTotals.GetValueOrDefault(a.AssetId) + a.Amount;

    var outputAssetTotals = new Dictionary<string, ulong>();
    foreach (var (_, assets) in assetOutputMap)
        foreach (var a in assets)
            outputAssetTotals[a.AssetId] = outputAssetTotals.GetValueOrDefault(a.AssetId) + a.Amount;

    var assetChange = new Dictionary<string, ulong>();
    foreach (var (assetId, inputAmount) in inputAssetTotals)
    {
        var outputAmount = outputAssetTotals.GetValueOrDefault(assetId);
        if (inputAmount > outputAmount)
            assetChange[assetId] = inputAmount - outputAmount;
    }

    // Assign asset change to the change output (the last one we appended)
    // ... build AssetGroups and Packet, append Packet.TxOut() to outputs
}
```

The exact implementation will depend on how `ConstructArkTransaction` handles `TxOut[]` — the asset OP_RETURN output must pass through as-is (amount=0, unspendable script). The existing code in `TransactionHelpers` already handles OP_RETURN counting.

**Step 2: Modify the `Spend(walletId, outputs)` overload (auto coin selection)**

When outputs have assets, extract `AssetRequirement` list and call the new coin selector overload.

**Step 3: Build and verify**

```bash
cd submodules/NNark && dotnet build
```

**Step 4: Commit**

```bash
cd submodules/NNark && git add -A && git commit -m "feat: asset-aware spending with packet construction"
```

---

## Task 12: Plugin Storage — VTXO AssetsJson Column

**Files:**
- Modify: `BTCPayServer.Plugins.ArkPayServer/Data/Entities/VTXO.cs`
- Modify: `BTCPayServer.Plugins.ArkPayServer/Storage/EfCoreVtxoStorage.cs`

**Step 1: Add AssetsJson property to VTXO entity**

In `VTXO.cs`, after `public string? ArkTxid { get; set; }` (line 25), add:

```csharp
public string? AssetsJson { get; set; }
```

Also update `GetHashCode()` to include `hash.Add(AssetsJson);`.

Also update `ToArkVtxo()` to pass Assets:

```csharp
Assets: string.IsNullOrEmpty(AssetsJson)
    ? null
    : JsonSerializer.Deserialize<List<VtxoAssetData>>(AssetsJson)
        ?.Select(a => new VtxoAsset(a.AssetId, a.Amount))
        .ToList()
```

Add a small DTO class (private or nested) for JSON deserialization:

```csharp
private record VtxoAssetData(string AssetId, ulong Amount);
```

**Step 2: Update EfCoreVtxoStorage.UpsertVtxo**

In `UpsertVtxo`, after setting `entity.ArkTxid`, add:

```csharp
entity.AssetsJson = vtxo.Assets is { Count: > 0 }
    ? JsonSerializer.Serialize(vtxo.Assets.Select(a => new { a.AssetId, a.Amount }))
    : null;
```

**Step 3: Update MapToArkVtxo**

In the private `MapToArkVtxo` method, add `Assets:` parameter:

```csharp
Assets: string.IsNullOrEmpty(entity.AssetsJson)
    ? null
    : JsonSerializer.Deserialize<List<VtxoAssetData>>(entity.AssetsJson)
        ?.Select(a => new VtxoAsset(a.AssetId, a.Amount))
        .ToList()
```

(Use a shared DTO record or anonymous type for deserialization.)

**Step 4: Build to verify**

```bash
cd /c/Git/NArk/.dev/worktree/quick-beacon && dotnet build BTCPayServer.Plugins.ArkPayServer
```

**Step 5: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Data/Entities/VTXO.cs BTCPayServer.Plugins.ArkPayServer/Storage/EfCoreVtxoStorage.cs
git commit -m "feat: add AssetsJson column to VTXO entity and storage"
```

---

## Task 13: Plugin — EF Core Migration

**Files:**
- Create: New migration file in `BTCPayServer.Plugins.ArkPayServer/Data/Migrations/`

**Step 1: Create migration**

```bash
cd /c/Git/NArk/.dev/worktree/quick-beacon
dotnet ef migrations add AddVtxoAssets --project BTCPayServer.Plugins.ArkPayServer --context ArkPluginDbContext
```

Or manually create the migration matching the pattern of existing ones. The migration should add a nullable `text` column `AssetsJson` to the Vtxos table.

Manual migration content:

```csharp
public partial class AddVtxoAssets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AssetsJson",
            schema: "BTCPayServer.Plugins.Ark",
            table: "Vtxos",
            type: "text",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AssetsJson",
            schema: "BTCPayServer.Plugins.Ark",
            table: "Vtxos");
    }
}
```

**Step 2: Build to verify**

```bash
cd /c/Git/NArk/.dev/worktree/quick-beacon && dotnet build BTCPayServer.Plugins.ArkPayServer
```

**Step 3: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Data/Migrations/
git commit -m "feat: add migration for VTXO AssetsJson column"
```

---

## Task 14: Plugin UI — Balance Asset Display

**Files:**
- Modify: `BTCPayServer.Plugins.ArkPayServer/Models/ArkBalancesViewModel.cs`
- Modify: `BTCPayServer.Plugins.ArkPayServer/Controllers/ArkController.cs` (GetArkBalances method)
- Modify: Balance-related views/view components

**Step 1: Add asset balances to ArkBalancesViewModel**

```csharp
public class ArkBalancesViewModel
{
    public decimal AvailableBalance { get; set; }
    public decimal LockedBalance { get; set; }
    public decimal RecoverableBalance { get; set; }
    public decimal UnspendableBalance { get; set; }
    public List<AssetBalanceViewModel> AssetBalances { get; set; } = [];
}

public class AssetBalanceViewModel
{
    public string AssetId { get; set; } = "";
    public string? Name { get; set; }
    public string? Ticker { get; set; }
    public ulong Amount { get; set; }
    public int Decimals { get; set; } = 8;
    public string TruncatedAssetId => AssetId.Length > 12 ? $"{AssetId[..6]}...{AssetId[^6..]}" : AssetId;
}
```

**Step 2: Compute asset balances in GetArkBalances**

In the `GetArkBalances` method of `ArkController.cs`, after computing BTC balances from spendable coins, aggregate asset balances:

```csharp
var assetTotals = new Dictionary<string, ulong>();
// Iterate over spendable coins (the ones contributing to AvailableBalance)
foreach (var coin in availableCoins)
{
    if (coin.Assets is not { Count: > 0 }) continue;
    foreach (var asset in coin.Assets)
    {
        assetTotals[asset.AssetId] = assetTotals.GetValueOrDefault(asset.AssetId) + asset.Amount;
    }
}

model.AssetBalances = assetTotals.Select(kv => new AssetBalanceViewModel
{
    AssetId = kv.Key,
    Amount = kv.Value,
}).ToList();
```

(Asset metadata like Name/Ticker can be resolved later via GetAssetDetails — skip for first pass.)

**Step 3: Update views to display asset balances**

In the relevant Razor views, add a section under the BTC balance that lists asset balances when non-empty. Keep it simple — a list of `{TruncatedAssetId}: {Amount}`.

**Step 4: Build and verify**

```bash
cd /c/Git/NArk/.dev/worktree/quick-beacon && dotnet build BTCPayServer.Plugins.ArkPayServer
```

**Step 5: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Models/ BTCPayServer.Plugins.ArkPayServer/Controllers/ BTCPayServer.Plugins.ArkPayServer/Views/
git commit -m "feat: display asset balances in UI"
```

---

## Task 15: Plugin UI — VTXO Asset Badges

**Files:**
- Modify: VTXO list views (the Razor view rendering `StoreVtxosViewModel`)
- Modify: `BTCPayServer.Plugins.ArkPayServer/Controllers/ArkController.cs` (Vtxos action)

**Step 1: Pass asset data through to VTXO view**

The VTXO list already shows VTXOs. When a VTXO has `AssetsJson`, deserialize and pass asset info to the view. Add asset badge rendering in the VTXO row — a small tag like `[AssetX: 100]` next to the BTC amount.

**Step 2: Build and verify**

```bash
cd /c/Git/NArk/.dev/worktree/quick-beacon && dotnet build BTCPayServer.Plugins.ArkPayServer
```

**Step 3: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/
git commit -m "feat: show asset badges on VTXOs"
```

---

## Task 16: Plugin UI — Send Wizard Asset Support

**Files:**
- Modify: `BTCPayServer.Plugins.ArkPayServer/Models/SendWizardViewModel.cs`
- Modify: `BTCPayServer.Plugins.ArkPayServer/Controllers/ArkController.cs` (Send actions)
- Modify: `BTCPayServer.Plugins.ArkPayServer/Views/Ark/Send.cshtml`

**Step 1: Add asset fields to SendOutputViewModel**

```csharp
public string? AssetId { get; set; }
public ulong AssetAmount { get; set; }
```

**Step 2: Update Send POST action**

When building `ArkTxOut[]` outputs, if the output has an `AssetId`, attach the `Assets` property:

```csharp
var arkOut = new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(amountSats), arkAddress)
{
    Assets = string.IsNullOrEmpty(output.AssetId)
        ? null
        : [new ArkTxOutAsset(output.AssetId, output.AssetAmount)]
};
```

**Step 3: Update the Send view**

Add an optional asset dropdown/input that appears when the wallet holds assets. When selected, the user specifies asset ID and amount alongside BTC amount.

**Step 4: Update coin suggestion and validation APIs**

The `suggest-coins` API endpoint should accept asset requirements and use the asset-aware coin selector.

**Step 5: Build and verify**

```bash
cd /c/Git/NArk/.dev/worktree/quick-beacon && dotnet build BTCPayServer.Plugins.ArkPayServer
```

**Step 6: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/
git commit -m "feat: add asset support to send wizard"
```

---

## Task 17: Plugin — Asset Metadata Cache

**Files:**
- Create: `BTCPayServer.Plugins.ArkPayServer/Services/AssetMetadataService.cs`
- Wire in DI registration

**Step 1: Implement simple in-memory cache**

```csharp
using System.Collections.Concurrent;
using NArk.Core.Transport;
using NArk.Core.Transport.Models;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class AssetMetadataService(IClientTransport clientTransport)
{
    private readonly ConcurrentDictionary<string, ArkAssetDetails> _cache = new();

    public async Task<ArkAssetDetails?> GetAssetDetailsAsync(string assetId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(assetId, out var cached))
            return cached;

        try
        {
            var details = await clientTransport.GetAssetDetailsAsync(assetId, cancellationToken);
            _cache.TryAdd(assetId, details);
            return details;
        }
        catch
        {
            return null;
        }
    }
}
```

**Step 2: Register in DI**

In the plugin's service registration, add:

```csharp
services.AddSingleton<AssetMetadataService>();
```

**Step 3: Use in balance display to resolve names**

Update `GetArkBalances` to optionally resolve asset metadata for display (name, ticker, decimals). If resolution fails, just show the truncated asset ID.

**Step 4: Build and verify**

```bash
cd /c/Git/NArk/.dev/worktree/quick-beacon && dotnet build BTCPayServer.Plugins.ArkPayServer
```

**Step 5: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/
git commit -m "feat: add AssetMetadataService with in-memory cache"
```

---

## Dependency Order

```
Task 1 (proto) ──────────────┐
Task 2 (VtxoAsset/ArkVtxo) ─┤
Task 3 (ArkCoin.Assets) ─────┤──→ Task 4 (transport mapping)
Task 6 (ArkTxOut.Assets) ────┘    Task 5 (GetAssetDetails)
                                        │
Task 7 (BufferR/W) ──→ Task 8 (core types) ──→ Task 9 (Packet)
                                                      │
Task 10 (coin selector) ──────────────────────→ Task 11 (spending)
                                                      │
Task 12 (plugin storage) ──→ Task 13 (migration) ────┤
                                                      │
Task 14 (balance UI) ──→ Task 15 (VTXO badges) ──→ Task 16 (send wizard)
                                                      │
Task 17 (metadata cache) ────────────────────────────┘
```

**Parallelizable groups:**
- Tasks 1-6 (model changes) can be done together
- Tasks 7-9 (encoding library) are sequential
- Tasks 12-13 (plugin storage) can parallel with NNark tasks
- Tasks 14-17 (UI) depend on storage being done
