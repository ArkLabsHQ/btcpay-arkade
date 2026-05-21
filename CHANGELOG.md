# Changelog

## [2.1.15] - 2026-05-21

### Features
- **Per-wallet diagnostic log download.** Operators can pull a focused per-wallet log from the store overview instead of grep'ing the whole BTCPay log for the right wallet id. The download button sits next to the wallet identity on the overview tile.
- **Boltz swap-creation tagged with the `btcpay-arkade` referral id.** Lets Boltz attribute volume back to btcpay-arkade for partner accounting; no behaviour change for the swap itself.
- **Co-installs cleanly with the BTCPay Electrum plugin.** The Electrum plugin rip-and-replaces NBXplorer's DI registrations and leaves `ExplorerClient.RPCClient` null; `NBXplorerBlockchain` would NRE on every chain-time / fee / broadcast call. The Arkade plugin's `IBitcoinBlockchain` factory now detects an NBXplorer-incompatible explorer provider (type-name `Electrum` *or* `RPCClient == null`) and falls back to `EsploraBlockchain` against the network's default Esplora endpoint (`ArkNetworkConfig.EsploraUri`, populated per-network from the canonical ts-sdk defaults via NNark #96). Mainnet / Mutinynet / Signet / Regtest all carry a working default; operators with a custom `ark.json` can override via `esplora`. Inbound on-chain events under Electrum require the companion `NewOnChainTransactionEvent` republish (Kukks/BTCPayServerPlugins#132). (#64)

### UX
- **Receive page polish.** Title now reads "Receive Arkade" instead of "Receive ARK". Post-generation "New X" buttons stack vertically as quiet text links (one label per line, no `payment-box` padding inflating the row) so the action sits secondary to the QR/address panel above it; the unmet-state "Generate X" stays prominent as the real CTA. The unified BIP-21 payment URI now embeds the boarding address (`bitcoin:<boardingAddress>?ark=<arkAddress>`, same shape the checkout uses) and the Link tab is the default — so a single QR scan drives both Arkade-aware and onchain-only wallets.
- **Brand: user-facing "Ark" → "Arkade" sweep.** 15 files / 46 strings across views (`InitialSetup`, `IntentBuilder`, `Contracts`, `ConfigurePayoutProcessor`, `StoreOverview`, `Send`, `Send2`, `Swaps`, `Vtxos`, `ListWallets`, `Intents`, `SpendOverview`, `LNPaymentMethodSetupTab`, `ArkadeMethodCheckout`) plus `ArkController` TempData / exception / error messages now consistently say "Arkade" in titles, headers, button text, help copy, and error messages. Code identifiers (`Ark` controller name, `ArkAddress`/`ArkVtxo` type names, the `?ark=` BIP-21 param, `BTC-CHAIN`) deliberately keep their shorthand.

### Bug Fixes
- **Send: "Max" no longer over-spends.** The Max amount wasn't subtracting the estimated fee, so build-intent then rejected the intent for missing fee headroom. The fee is now deducted from Max before the request.
- **Send no longer crashes the plugin when Bitcoin Core RPC blips during fee estimation.** Transient RPC failures now degrade gracefully instead of throwing through the controller.
- **Checkout: third-party BIP-21 params preserved on the Arkade tab.** Plugins like PayJoin (`pj=`) and Branta (`branta_*`) gate on `model.PaymentMethodId == "BTC-CHAIN"` and so were silently dropped from the Arkade BIP-21. The Arkade checkout extension now synthesises a bitcoin-tab pipeline, runs the other global extensions against it, and forwards their additions to the Arkade URI verbatim.
- **Initial-setup wizard has a close (X) button.** Bailing out mid-import no longer requires hitting Back.
- **Invoices listing page hung indefinitely once the checkout extension started harvesting upstream BIP-21 params.** `ArkadeCheckoutModelExtension` is itself registered as `IGlobalCheckoutModelExtension` (so it can hide LN/LNURL tabs when Arkade is shown) AND its constructor injected `IEnumerable<IGlobalCheckoutModelExtension>` (to replay other plugins' bitcoin-tab contributions on the Arkade tab). The enumerable resolution must build every implementation before our ctor returns — including ourselves — producing a DI cycle. `Microsoft.Extensions.DependencyInjection.ServiceLookup.StackGuard` papered over the recursion by re-running each layer on a fresh thread (`WaitHandle.WaitOne` on the caller); a single request to `/stores/{id}/invoices` stalled six threads on the same factory before the page failed to write a single response byte (TTFB never observed). Switched to `IServiceProvider` + lazy `GetServices<IGlobalCheckoutModelExtension>()` at enumeration time — the singleton is cached by the time we look, so the cycle never forms.
- **Boarding payment's `payment.Destination` got persisted as the off-chain Ark address (root cause of the previous bullet).** `HandlePaymentData` was JObject-patching `Blob2["Destination"]` (capital D) after `Set(...)` initialised the top-level field from the prompt. BTCPay's default JSON serialiser is camelCase, so the on-disk property is `"destination"` — JObject is case-sensitive, so the patch added a sibling key the deserialiser ignored and the prompt's Ark address survived. Switched to the lowercase `"destination"` key and remove any stale capital-D sibling left by earlier runs. Existing rows already store the correct boarding address inside `details.destination`, so the view fallback now reads from there before falling back to the (incorrect) top-level field.
- **Invoice "Arkade Payments" table mis-rendered boarding payments as VTXO payments.** A payment that arrived via the on-chain boarding address shared the invoice's prompt details with the off-chain Ark path, so the view pulled the prompt's Ark address + offchain Payment contract for the row even when the actual settlement was on-chain boarding. The outpoint (a real BTC txid) was linked through the Arkade explorer instead of the BTC chain explorer. Fix: persist `IsBoarding` on `ArkadePaymentData` and `BoardingContractString` on `ArkadePromptDetails`, render a "Boarding" / "VTXO" type badge per row, and route the address + outpoint links to `TransactionLinkProviders["BTC-CHAIN"]` when the row is on-chain. Old payment rows lacking the flag fall back to a destination-equality check against the prompt's known boarding address.
- **Invoice settlement only deactivated the Payment contract; Boarding stayed Active forever.** `ConfigurePrompt` tags both the Payment contract (the offchain Arkade address) AND, when boarding is enabled, the Boarding contract with `Source = "invoice:{id}"`. The invoice listener's `ToggleArkadeContract` only toggled the contract whose script matched the prompt's `GetArkAddress()` — i.e. the Payment one — so the boarding entry kept being polled, kept showing up under "active contracts," and prevented the script subscription from unsubscribing. Now toggles every contract carrying `Source = "invoice:{id}"`. HTLC contracts (from Lightning checkout) use a `swap:{id}` Source tag and are still driven by `OnSwapChanged` based on swap state — out of scope for this fix.
- **Cheat-mode "Pay Invoice" failed with `No such container: ark`.** The arkd v0.9 wallet split moved the daemon's container name from `ark` to `arkd` (the sidecar `ark-wallet` now owns the signer). Nigiri 0.5.14's `ark` subcommand still does `docker exec ark <args>` against the old name, so every cheat-mode call (send, receive, settle) failed at the docker daemon. Replaced the `nigiri ark …` calls with `docker exec arkd ark …` (container name overridable via `ARKADE_CHEAT_ARK_CONTAINER` env var). Nigiri's `rpc` and `faucet` paths — which talk to bitcoind, not the arkd container — are untouched. As a side effect, the recovery path's broken shell substitution (`$(nigiri ark receive | jq -r ...)` never expanded under `Process.Start`) is now a proper two-step call: parse JSON in C#, pass the address into `nigiri faucet`.

### Refactor
- **Manual receive/boarding contracts share a single `Source = "manual"` metadata value.** The boarding flow was tagging contracts with `"manual-boarding"` and the receive flow with `"manual"`, even though the `ContractType` (`"Boarding"` vs `"Payment"`) already discriminates the two. Both call sites now write `"manual"`; `FindManualReceiveAddress` adds a `contractTypes: [ArkPaymentContract.ContractType]` filter so it can't accidentally cross-match a boarding entry. `FindManualBoardingAddress` and the Contracts view's badge accept the legacy `"manual-boarding"` value, so existing wallets keep displaying their manual address without a data migration.
- **Boarding minimum: default raised to 5000 sats, dust floor centralised.** The hard-coded `330` sprinkled across `ArkadePaymentMethodConfig`, `ArkController.save-boarding`, and `StoreOverview.cshtml` was both the seeded default and the P2TR dust floor — two concepts collapsed into one literal. Now: `ArkadePaymentMethodConfig.DefaultMinBoardingAmountSats = 5000L` (default for new stores) and `ArkadePaymentMethodConfig.P2trDustLimitSats = 330L` (floor; users still cannot configure below it). All callers reference the constants; the form `min` attribute and help text interpolate them. Existing stores keep whatever value they persisted — only new stores get the 5000-sat default.

### SDK (NNark)
- **VTXO sync responsiveness — one immediate poll per stream push, no exponential backoff schedule.** Stream pushes used to trigger a 750 ms / 3 s / 8 s retry ladder before giving up; on a healthy connection that was just three redundant polls. Now a single immediate poll per push, which is what every consumer (invoice listener, swap watcher, etc.) actually wants — perceived latency drops to one network round-trip without the spurious "still nothing" log churn (#99).
- **Per-receive claim ~800× faster via cached `OutputDescriptor.Parse` + BIP-39 master ExtKey.** Hot-path `WalletFactory` work was re-parsing the same descriptor string and re-running BIP-39 KDF on every claim. Both are deterministic and per-wallet, so they're now memoised. Largest improvement on HD wallets where the KDF dominated cold claim cost (#100).
- **`ImportContract` / `DeriveContract` info logs now include contract type + script.** Multi-derive flows (invoice payment + boarding co-derivation, swap restore) are diagnosable at INFO without dropping to DEBUG (#97).
- **Per-network Esplora / Electrum endpoint defaults on `ArkNetworkConfig`.** New nullable `EsploraUri` / `ElectrumWsUri` / `ElectrumTcpUri` fields carrying the canonical ts-sdk defaults per network — apps that need an `IBitcoinBlockchain` without NBXplorer/bitcoind (e.g. the Arkade plugin's Electrum-fallback above) can pull the right endpoint straight off the preset. Electrum TCP endpoints are protocol-verified against the live public Fulcrum hosts (`server.version` → `{"result":["Fulcrum 2.1.0","1.4"]}`); 50001 plain TCP is the only port the public hosts actually expose, 50002 TLS is closed (use WSS at the host's port 443 via `ElectrumWsUri`). Regtest fills `electrs`'s `50000` binary-protocol port (NOT `30000`, which is electrs's HTTP REST — different protocol) (#96).
- **Deterministic asset packet serialization + cross-SDK fixture parity.** `AssetPacketBuilder` now emits groups in a stable order (by asset id, then group index) regardless of input order, matching rust-sdk and ts-sdk so packets are reproducible and fixture-comparable across SDKs. Plus the 4 ts-sdk cross-SDK fixture files NNark lacked (`asset_ref` / `asset_input` / `asset_output` / `metadata`) imported with fixture-driven tests (393/393 green) (#94).
- **Quieter VTXO sync at idle.** `VtxoSynchronizationService`'s 5-second safety-net poll no longer logs at `Info` on every empty tick — `Info` is kept only for productive polls (a VTXO landed) and the one-off cold-start catch-up. Stops 24 INFO lines/minute of pure noise from drowning the log on an idle wallet (#95).
- **Boltz: `AddBoltzProvider` self-contained.** Direct-DI consumers (no `ArkApplicationBuilder`) now get `BoltzClient` registered as part of `AddBoltzProvider` instead of an opaque "Unable to resolve BoltzClient" error (#93).
- **Unilateral exit support.** The non-cooperative path from a VTXO to an on-chain UTXO is now exposed by the SDK — recovery is possible without the Arkade operator's cooperation (#39).
- **HD wallet gap-limit recovery via modular discovery providers.** Plugin can re-derive used addresses from a mnemonic without prior local state (#77).
- **Per-wallet VTXO sync cursor in wallet metadata.** Cold-start catch-up window is "since last shutdown" instead of "all of history" — large wallets warm-start in seconds instead of re-fetching every VTXO (#78).
- **Multi-provider swap architecture.** Boltz remains the concrete provider; the abstraction admits future swap providers without invasive changes (#79).
- **Recovery: reconcile pending Arkade transactions stranded between Submit and Finalize.** Crash/timeout windows between submit and finalize now self-heal on next sync (#90).
- **Swap + batch log entries scoped to `WalletId`.** Multi-wallet servers can grep logs cleanly per wallet (#84).
- **Single persistent Boltz websocket** with subscribe/unsubscribe per swap (was one ws per swap; exhausted file handles on busy stores) (#83).
- **Swaps marked Failed after 10 consecutive Boltz 404s** instead of hanging forever (#80).
- **Blockchain: chain-time cached with fallback** on transient Bitcoin Core RPC failure (#85).
- **EF Core: opt-in `DateTimeOffset → long ticks` mapping** so SQLite `ORDER BY` works correctly on persisted timestamps (#92).

## [2.1.14] - 2026-04-24

### Bug Fixes
- **Faster detection when arkd's subscription push is ahead of its indexer.** v2.1.13 added a 30-second safety-net poll, but user's repro showed arkd's indexer sometimes takes ~28 seconds to make a VTXO queryable after the subscription event fires — invoice detection was still noticeably slow. Tightened to 5-second interval, and each stream push now fires three polls (750ms / 3s / 8s) instead of one to cover the full observed commit-lag range.
- **Explorer links pointed to the arkd operator instead of arkade.space.** `ArkPlugin.LoadNetworkConfig` dropped `ExplorerUri` when merging `ark.json` with the preset; the Mainnet preset's `https://arkade.space` never reached the plugin views, so the helper fell through to the `ArkUri/v1/indexer/vtxos?...` fallback. Signet preset also didn't set `ExplorerUri`. Both fixed.

### Performance
- `_readyToPoll` channel moved from Bounded(5) to Unbounded so stream-event processing never back-pressures on a full queue when retry schedules + RoutinePoll coincide.

## [2.1.13] - 2026-04-24

### Bug Fixes
- **Invoice stays pending after customer pays — arkd's script-subscription sometimes doesn't push VTXO events.** Reproduced: paid VTXO was visible via arkd's REST indexer but the plugin never received a stream push for the invoice's Receive script. Two related fixes:
  1. **Routine safety-net poll every 30s.** `VtxoSynchronizationService` now re-queries all active scripts with a 2-minute `after` window. Cheap (recent changes only, no historical re-fetch) and catches any event arkd silently drops from the subscription stream.
  2. **750ms delay between stream push and follow-up poll.** arkd can emit the event before its indexer query path has committed the VTXO; immediate polls returned the stale prior state and the stream never fired again. The brief delay lets the indexer catch up.

### Performance
- **Stream pushes no longer re-fetch full VTXO history per touched script.** Previously every arkd push re-queried the whole script's VTXO history (observed: 2849 VTXOs fetched for 2 scripts in 5s on every push, mostly unchanged). Stream-triggered polls now use a 5-minute `after` window; `UpdateScriptsView` only full-fetches the *newly-added* scripts instead of the whole subscribed set.

## [2.1.12] - 2026-04-24

### Observability
- **Full stream→upsert chain now visible at Info level.** Previous logs showed "arkd pushed update" at Info but then went quiet until the swap detection path fired again. Promoted `StartQueryLogic` poll-entry and poll-result (with count and elapsed ms) to Info; `EfCoreVtxoStorage.UpsertVtxo` now logs inserted/updated at Info with outpoint, script, amount, and spent/settled flags. No-op writes stay at Debug. This is purely diagnostic — no behavioural changes.

## [2.1.11] - 2026-04-24

### Bug Fixes
- **VTXO subscription pipeline could silently die**, which matches the "stream isn't working" symptom. Three leaks plugged in `VtxoSynchronizationService`:
  1. **Graceful stream end** — if arkd closed `GetSubscription` without throwing, the loop exited, `_streamTask` completed successfully and nothing restarted. Now detected and restarted via `UpdateScriptsView`.
  2. **`StartQueryLogic` had no error handling** — a single transport/storage exception killed the task permanently, and every subsequent stream event piled up in the `_readyToPoll` channel with no reader. Now each poll iteration is try/caught and the loop survives.
  3. **No observability on stream events** — the subscribe was logged at Debug and nothing else. Now each arkd push logs the scripts at Info, plus poll-result counts at Debug.

## [2.1.10] - 2026-04-24

### Observability
- **Verbose logging around swap state transitions.** After the recent detection fixes it was still hard to tell from the logs whether `OnVtxosChanged` had dispatched to a swap, whether `_scriptToSwapId` was populated on time, and what Boltz was actually reporting on each poll. Added info-level log lines for: VTXO-to-swap dispatch, script→swap map updates/evictions, every Boltz status probe, and how many VTXOs arkd returned during the per-swap contract-script refresh.

## [2.1.9] - 2026-04-24

### Bug Fixes
- **Swap still stalls when arkd's subscription stream drops / misses the VTXO-arrival event.** v2.1.8 fixed the internal script→swap map, but that only helps if `OnVtxosChanged` fires — and it only fires if the VTXO actually reaches `VtxoStorage`. arkd's subscription doesn't retroactively replay, so a VTXO that arrives between "subscribe" and "first read of the stream" (or during a reconnect) is invisible to us. `SwapsManagementService.PollSwapState` now does a direct `GetVtxoByScriptsAsSnapshot` call for the swap's contract script on every non-terminal status probe — same pattern as the existing refund path — so we catch anything the stream missed.

## [2.1.8] - 2026-04-24

### Bug Fixes
- **Invoice listener NRE on prompts with no details.** `ArkContractInvoiceListener.GetListenedArkadeInvoice` blindly passed `prompt.Details` through to `ArkadePaymentMethodHandler.ParsePaymentPromptDetails`, which `ToObject<>`s it and NREs when null. Now returns `null` if the prompt has no details.
- **Swap stuck "pending" when VTXO arrived before first poll.** `SwapsManagementService._scriptToSwapId` was only populated inside `PollSwapState`, so VTXOs arriving on a swap contract before the first `RoutinePoll` (1-minute cadence) — or in the race window immediately after `InitiateReverseSwap` / `InitiateBtcToArkChainSwap` — fell through `OnVtxosChanged` and never triggered a swap-state refresh. Seeded at startup from storage and kept in sync with `SwapsChanged` synchronously.

### SDK (NNark)
- REST transport pagination off-by-one fix (mirrors the gRPC fix from v2.1.7).

## [2.1.7] - 2026-04-24

### Bug Fixes
- **Wallet import was actually silently capping at exactly 11 × 1000 = 11000 VTXOs, not 121M-row stalling.** arkd's paginator is 1-based and clamps the response's `next` to `total` on the final page; the client's loop condition `Next != Total` therefore exited one page early, dropping the last page entirely. Switched to `Current < Total`. The previous 2.1.6 `ActiveScriptsChanged` fix is still correct — it removed the quadratic event cascade — but was insufficient by itself because the underlying pagination was dropping pages.

## [2.1.6] - 2026-04-24

### Bug Fixes / Performance
- **VTXO import on wallets with deep history stopped around ~11k entries.** `UpsertVtxo` was firing `ActiveScriptsChanged` on every row, which ran `VtxoSynchronizationService.UpdateScriptsView` → `IVtxoStorage.GetActiveScripts` (whose default implementation scans every unspent VTXO). 11k upserts × a full VTXO scan each is ~121M row reads — the sync appears to stall. Fixed by not firing the event from VTXO upserts (VTXOs only ever arrive on scripts we already know about, so a new VTXO cannot introduce a new script).

## [2.1.5] - 2026-04-24

### Bug Fixes / Performance
- **Time-window post-op VTXO polling.** After a spend, the plugin was asking arkd's indexer for *all* VTXOs on every active contract script, which scales with a wallet's whole history. Post-spend and post-batch catch-up now passes a 5-minute `after` filter so arkd only returns recently-changed VTXOs. Explicit user actions ("Sync wallet", "Import contract", etc.) still do a full poll.

### SDK (NNark)
- `VtxoSynchronizationService.PollScriptsForVtxos(scripts, after, ...)` — new overload that forwards the `after` filter to the arkd indexer via `GetVtxoByScriptsAsSnapshot`.
- `PostSpendVtxoPollingHandler` and `PostBatchVtxoPollingHandler` now pass a 5-minute `after` window to the indexer.

## [2.1.4] - 2026-04-24

### Bug Fixes
- **Initial-setup timeout when importing an nsec with a long history.** The handler was synchronously polling arkd once per known contract during import; wallets with hundreds of prior contracts blew past the HTTP timeout. The VTXO and boarding-UTXO catchup now runs on a background task so the user is redirected immediately; funds appear as the sync completes.

## [2.1.3] - 2026-04-24

### Bug Fixes
- **Imported nsec wallet shows empty VTXOs / Contracts / Intents / Swaps pages.** `GetFromInputWallet` was returning `IsOwnedByStore=false` when the imported nsec matched a wallet already present in storage (e.g., re-importing after a failed attempt or importing the same key on a second store). Presenting the raw nsec is itself proof of ownership, so `IsOwnedByStore=true` now applies in that case too. The home-screen balance was unaffected since it doesn't gate on `GeneratedByStore`; the management pages were, which is why they silently rendered empty.

## [2.1.2] - 2026-04-24

### SDK (NNark)
- Disable one-by-one VTXO script polling workaround (reverted now that arkd's multi-script query is fixed)

## [2.1.1] - 2026-04-24

### Breaking Changes
- **Minimum BTCPay Server version raised to 2.3.8** (up from 2.3.7)

### Bug Fixes
- **Plugin no longer crashes BTCPay when arkd is unreachable on startup.** `SwapsManagementService` now defers the arkd readiness check to a background retry loop instead of blocking `StartAsync`; the host comes up cleanly and swap operations queue until arkd is available.
- **Quieter logs while arkd warms up.** gRPC `FailedPrecondition` ("wallet is locked or syncing") from the batch event stream now logs a single-line warning with the retry cadence instead of a full `Error` stack trace every 5 seconds.
- **DI validation fix:** `DelegationService` no longer fails to construct when the plugin doesn't opt into delegation (it was requiring `IDelegatorProvider` that is only registered by `AddArkDelegation`).

### SDK (NNark)
- **Vendored `NBitcoin.Scripting.OutputDescriptor`** into `NArk.Abstractions/Scripting/` — NBitcoin 10 removed the classic descriptor subsystem in favor of BIP388 Wallet Policies. NArk continues to use `OutputDescriptor` so 33-byte compressed keys with correct parity flow through the whole stack (arkd → SignerKey → contracts → MuSig2).
- **Opt-in payment tracking:** `AddArkPaymentTracking()` + `ConfigureArkPaymentEntities()`. Consumers that don't need payment-request tracking carry no extra schema or services.
- **Opt-in delegation:** `DelegationService` / `IDelegationTransformer` moved from `AddArkCoreServices` into `AddArkDelegation` so plugins without a Fulmine delegator aren't forced to register unreachable services.
- **Regtest tooling:** `clean-env.sh` now chowns nigiri volumes via a throwaway root container before `nigiri stop --delete`, eliminating the "openfdat ... permission denied" postgres warning during teardown.
- **Docs refresh:** articles updated to match current APIs (real SwapsManagementService / IAssetManager / ConfigureArkEntities / regtest path).

## [2.1.0] - 2026-04-10

### Breaking Changes
- **Migrate to .NET 10**: target framework updated from `net8.0` to `net10.0`; requires BTCPay Server >= 2.3.7

### Features
- **DocFX documentation**: API reference docs with GitHub Pages deployment (`docs.yml` workflow)
- **Wallet sample app**: Blazor WASM wallet with EF Core SQLite (BeSql), QR codes, Lightning receive, LNURL, settings, backup/restore, smart send, and contract display

### Improvements
- Package dependency updates (Microsoft.Extensions.* 10.0.4, NBitcoin 9.0.5, etc.)

### SDK
- NNark: dedicated list and detail pages, contract persistence fix
- NNark: backup support, smart send, LNURL integration, server info display
- NNark: wallet UI polish — QR styling, asset receive mode, VTXO-gated minting
- NNark: wallet features — real QR codes, Lightning receive, settings page
- NNark: fix wallet creation, mutinynet network name resolution, SPA 404 fallback
- NNark: payment tracking repository for outbound payments and inbound requests
- NNark: `UnknownArkContract` handling fix in spending and sweeper services
- NNark: `BoardingAllowed` and vtxo/utxo amount bounds from server info
- NNark: `GetVtxosByOutpoints` transport method + VTXO polling race fix

## [2.0.5] - 2026-03-25

### Bug Fixes
- **Critical: fix plugin crash on SuggestCoins/ValidateSpend endpoints** — missing `[Authorize(Policy = CanModifyStoreSettings)]` attribute meant BTCPay's store-resolution filter never ran, causing `StoreData is not set` exception that disabled the entire plugin server-wide

### Other
- Add detailed README and MIT license (#41)

### SDK
- NNark: fix `ComputeExpiresAt` incorrect `IsRBF` guard (1292501)
- NNark: CI permissions fix for publish job (9d06e8b)
- NNark: cache nigiri binary to fix E2E infrastructure timeouts (3254221)
- NNark: various test assertion and capture fixes

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
