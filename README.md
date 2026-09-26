# Arkade for BTCPay Server

> Accept Bitcoin payments through [Arkade](https://arkadeos.com) — a self-custodial, off-chain Bitcoin Layer 2 — directly inside BTCPay Server.

[![Version](https://img.shields.io/badge/version-2.1.14-blue)](CHANGELOG.md)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![BTCPay Plugin](https://img.shields.io/badge/BTCPay%20Server-Plugin-orange)](https://btcpayserver.org)

---

## What Is This?

**btcpay-arkade** is a BTCPay Server plugin that integrates [Arkade](https://arkadeos.com) as a payment method. It lets merchants accept instant, low-fee Bitcoin payments off-chain while retaining full self-custody — no Lightning node required, no custodian involved.

Payments are settled through **Virtual UTXOs (VTXOs)**, Arkade's off-chain Bitcoin outputs that are cryptographically anchored to real Bitcoin and can be unilaterally exited to the base chain at any time.

---

## Payment Flows

### 1. Arkade Native
Direct VTXO-to-VTXO off-chain payments within the Arkade network. Instant settlement, zero routing fees. Payers need an Arkade-compatible wallet.

### 2. Lightning via the Arkade corridors
Payers with Lightning wallets pay a BOLT11 invoice. The plugin settles it over an Arkade intent corridor: it negotiates a quote with an Arkade swap solver per payment, and both sides settle into a covenant — receiving takes delivery of one the solver funded, sending funds a lockup the solver takes by revealing the preimage. Trustless, and no Lightning node on the merchant side.

### 3. Boarding Address
Payers send on-chain Bitcoin to a Taproot "boarding address." The Arkade operator batches this into the next batch, converting the on-chain UTXO into a VTXO. If the operator is unresponsive, the payer can reclaim funds unilaterally after a timelock.

The checkout page presents all applicable methods in a single BIP-21 QR code, letting any wallet pay automatically.

---

## Architecture

```
BTCPay Server
└── Arkade Plugin
    ├── ArkController              # HTTP endpoints for store management
    ├── ArkContractInvoiceListener # Monitors contract state → updates invoice status
    ├── BoardingTransactionListener# Watches on-chain boarding UTXOs via NBXplorer
    ├── ArkadeSpendingService      # Sends payments (payouts, refunds)
    └── NNark (submodule)          # .NET Arkade SDK
        ├── NArk.Core              # Wallet, VTXO logic, HD/SingleKey signers
        ├── NArk.Arkade             # Arkade Script contracts
        ├── NArk.ArkadeIntents      # Non-interactive swap corridors (RFQ + covenants)
        └── NArk.Storage.EfCore     # PostgreSQL persistence (EF Core)
```

The plugin persists all state (VTXOs, contracts, swaps, intents, wallets) in BTCPay's existing PostgreSQL database via EF Core migrations.

---

## Requirements

- **BTCPay Server** (self-hosted, any recent version)
- **PostgreSQL** (bundled with standard BTCPay deployments)
- **Arkade server (arkd)** v0.9.0 or later — accessible over gRPC from your BTCPay host
- **.NET 10** SDK (if building from source)

> ⚠️ **Alpha software.** This plugin is actively developed and not yet recommended for high-value production deployments. Always maintain a backup of your seed phrase.

---

## Installation

### Via BTCPay Plugin Manager (Recommended)

1. Open your BTCPay Server instance
2. Go to **Server Settings → Plugins**
3. Search for **"Arkade"**
4. Click **Install** and restart when prompted

### From Source

```bash
git clone https://github.com/ArkLabsHQ/btcpay-arkade.git
cd btcpay-arkade
./setup.sh        # Pulls submodules, restores workloads, publishes plugin
```

On Windows:
```powershell
.\setup.ps1
```

The setup script will:
- Pull the `submodules/btcpayserver` submodule
- Restore .NET workloads
- Create a plugin entry in your BTCPay config
- Publish the plugin to the correct location

---

## Setup

### 1. Configure Your Store

1. Navigate to your BTCPay store → **Settings → Arkade**
2. Enter your **Arkade server URL** (e.g. `https://arkd.yourdomain.com`)
3. Import your wallet:
   - **HD Wallet**: paste a BIP-39 mnemonic (12 or 24 words)
   - **SingleKey Wallet**: paste a Nostr `nsec` private key

### 2. Configure Payment Methods

1. Go to **Store Settings → Payment Methods**
2. Enable **Arkade** as a payment method
3. Optionally enable **Lightning** — available once an Arkade swap solver can be reached (see [Lightning configuration](#lightning-configuration))

### 3. Store Settings

| Setting | Default | Description |
|---|---|---|
| Boarding Address | Enabled | Show boarding address on invoices (on-chain entry to Arkade) |
| Boarding Minimum | 5000 sats | Minimum amount to display boarding address (floor: 330 sats / P2TR dust) |
| Sub-dust Payments | Disabled | Accept payments below 330 sats (no dust limit for VTXOs) |
| Auto-sweep Address | — | Forward all received funds to this on-chain address automatically |
| Swap Fee | Store pays | Who covers the solver's spread on a receive swap, Lightning or onchain (see below) |

**Swap Fee** decides which leg of a receive swap the order amount pins, and the two sides differ for
the payer:

- **Store pays** (default): the payer is billed the order amount — the invoice is minted for it, and the
  onchain swap's HTLC asks for it — and the solver's fee comes out of what the store receives. The
  invoice is credited with what the payer sent, as it is on any Lightning corridor.
- **Sender pays**: the store receives the order amount in full and the payer is billed the fee on top.
  A Lightning invoice then exceeds the amount the customer approved, which LUD-06 wallets refuse. The
  onchain swap is not offered at all in this mode — it would need an amount the other rails do not
  share, and one BIP21 carries a single amount — so onchain payers use the boarding address, which
  takes the order amount and charges no fee.

A store can run a watch-only Arkade wallet, so the plugin enables the SDK's `SignerlessFallback`: a
claim or refund such a wallet cannot sign goes through the covenant's signerless leaf instead, which
pays the same pinned address and is co-signed by the covenant emulator.

### 4. Lightning configuration

Lightning is settled over the Arkade intent corridors, configured in the same `ark.json` the
Arkade network endpoints come from:

```json
{
  "emulator": "https://emulator.arkade.sh",
  "solver-relay": "wss://relay.example.com",
  "solver-pubkey": "<x-only hex>",
  "covclaimd": "https://covclaimd.example.com"
}
```

| Key | Required | Description |
|---|---|---|
| `emulator` | Yes | The covenant emulator that co-signs both corridors' scripts. Its key is one of the parameters the lockup commits to, so without it no corridor can derive an address at all. Defaulted per network for mainnet, mutinynet and regtest. |
| `solver-relay` | No | The Nostr relay a named solver is reached on. Both sides dial out; neither listens. |
| `solver-pubkey` | No | A named solver's x-only public key — its identity on the relay. |
| `covclaimd` | No | A claim daemon a receive swap seals its preimage to, so a claim can finish while this server is down. |
| `max-fee-bps` | No | The most a solver may charge per swap, in basis points of the amount paid in. Default `100` (1%). |
| `max-fee-flat-sats` | No | A fixed allowance added on top of `max-fee-bps`, mainly for the L1 miner fee on the onchain corridor. Default `500`. |

`solver-relay` and `solver-pubkey` are only read together: name both to pin one counterparty,
or leave both out and a solver is chosen per payment from the network's public registry. A
development stack has to name one — its solver mints a fresh identity per run, so nothing can
list it.

Every quote is held to that limit, whichever solver it came from. A quote over it is refused
before anything is funded or handed to a payer: a Lightning invoice is not minted, a payment is
not made, and checkout drops the onchain swap (offering boarding if it is on). A solver chosen from the
registry is additionally held to the fee and limits on its own card.

"Configured" here means configured, not connected: both sides of the transport dial out, so the
first evidence a solver is really there is a quote coming back.

---

## Wallet Types

### HD Wallet (BIP-39 Mnemonic)
- Full hierarchical deterministic key derivation
- Unique address per invoice (BIP-44 style)
- Supports boarding addresses (requires HD derivation)
- Recommended for merchants

### SingleKey Wallet (Nostr nsec)
- Single static key — all contracts derive from one key
- Simpler setup
- Boarding addresses not supported
- Suitable for lightweight deployments

### Watch-Only Wallet (Account Descriptor)
- No signing material stored on the server — the merchant pastes a
  Taproot account descriptor (e.g. `tr([fingerprint/86'/0'/0']xpub.../0/*)`
  for HD style, or `tr(pubkey)` for single-key style) and the plugin
  observes the wallet by deriving addresses and watching VTXOs.
- Read-only operations work out of the box: receive, balance display,
  invoice payment detection, contract listing.
- **Signing-dependent operations** (batch participation, unilateral
  exits, payouts) require a remote signer. Install the companion
  `BTCPayServer.Plugins.App` plugin and pair a BTCPayApp device — the
  device holds the private key and signs over a SignalR bridge. Without
  a paired device the wallet is still useful for monitoring; signing
  calls fail with a descriptive `"install the App companion plugin"`
  error scoped to the operation, not to startup.
- Setup: in the initial-setup wizard, pick **Pair a watch-only wallet**
  under *I have a wallet* and paste the descriptor. Example:
  ```
  tr([abcd1234/86'/0'/0']xpub6CUGRUonZSQ4TWtTMmzXdrXDtypWKiKrhko4egpiMZbpiaQL2jkwSB1icqYh2cfDfVxdx4df189oLKnC5fSwqPfgyP3hooxujYzAu3fDVmz/0/*)
  ```

---

## Features

### Invoices & Checkout
- Unified BIP-21 QR code covers Arkade native + Lightning in one scan
- NFC-compatible checkout component
- Real-time payment status updates via VTXO subscription
- Boarding payments show as "Processing" until 1 on-chain confirmation, then "Settled"
- Sub-dust toggle for micro-payments

### Wallet Management
- Import via mnemonic or nsec
- View balance in BTC with show/hide privacy toggle
- Clear wallet configuration without losing on-chain funds
- Contract sync on import

### VTXO Management
- List, filter, and inspect all virtual UTXOs
- Track spend status, expiry, and settlement transaction
- VTXO asset support (Arkade programmable assets)

### Intent & Batch System
- Construct batch payment intents with multiple outputs
- Intent builder UI with fee breakdown
- Unified Send wizard: QR scanning, BIP-21 parsing, multi-output, manual coin selection

### Swaps (Lightning ↔ Arkade)
- Arkade intent corridors for incoming Lightning payments (the solver funds a covenant the store claims)
- Arkade intent corridors for outgoing Lightning payments (the store funds a lockup the solver takes)
- A solver quoted per payment — named in `ark.json`, or chosen from the public solver registry
- Real-time swap lifecycle monitoring, on the **Swaps** page
- LNURL-pay destination support

### Payouts
- Process Arkade payouts through BTCPay's native payout system
- Payout tracking integration in the Send wizard

### Dashboard
- "Recent Activity" widget: latest VTXOs, intents, and swaps at a glance
- Balance display on BTCPay store overview

---

## Development

### Prerequisites
- .NET 10 SDK
- Docker (for test environment)
- PostgreSQL

### Running Tests

After running `setup.sh`, start the local regtest environment (Bitcoin + arkd + Fulmine) — a cross-platform Node CLI, no WSL required:
```bash
node submodules/NNark/regtest/regtest.mjs start --profile delegate
```

The Lightning corridors need more of the stack — a covenant emulator, a claim daemon and a swap solver:
```bash
node submodules/NNark/regtest/regtest.mjs start --profile emulator,covclaimd,solver,lightning
```

On Windows (wraps the same CLI; extra arguments pass through, e.g. `start-test-env stop`):
```cmd
start-test-env.cmd
```

This spins up a regtest Bitcoin node, an Arkade server, and supporting services locally. Then run the E2E test suite:
```bash
dotnet test NArk.E2E.Tests/NArk.E2E.Tests.csproj
```

### Adding EF Core Migrations

```bash
./add-migration.sh <MigrationName>
# or on Windows:
.\add-migration.ps1 <MigrationName>
```

### Project Structure

```
btcpay-arkade/
├── BTCPayServer.Plugins.ArkPayServer/   # Main BTCPay plugin
│   ├── Controllers/                     # HTTP controllers
│   ├── Data/                           # EF Core entities & migrations
│   ├── Models/                         # View models
│   ├── Services/                       # Background services
│   ├── Views/                          # Razor views
│   └── PaymentHandler/                 # BTCPay payment method integration
├── NArk.E2E.Tests/                     # End-to-end test suite
├── submodules/
│   ├── NNark/                          # .NET Arkade SDK (NArk.Core / .Swaps / .Storage.EfCore)
│   └── btcpayserver/                   # BTCPay Server source (build dependency)
├── docs/                               # Internal design documents
├── setup.sh / setup.ps1               # First-time setup scripts
└── add-migration.sh / .ps1            # EF Core migration helpers
```

### Release Process

CI automatically creates a GitHub Release with the changelog body when a version tag is pushed:
```bash
git tag v2.1.14
git push origin v2.1.14
```

---

## Ark Protocol Concepts

### VTXOs (Virtual UTXOs)
Off-chain Bitcoin outputs secured via collaborative (user + operator) and unilateral (timelocked) Taproot spending paths. VTXOs are the atomic unit of value in Arkade. The operator can never steal them — the unilateral exit path is always available.

### Batches & Commitment Transactions
Periodically, the Arkade operator batches pending VTXOs into an on-chain **commitment transaction**, anchoring off-chain state to Bitcoin. This is how off-chain payments get Bitcoin-level finality.

### Contracts
Payment flows are modeled as Taproot contracts derived from a wallet descriptor and derivation index. Each invoice gets a unique contract address.

### Boarding Addresses
Taproot addresses that serve as the entry point into Arkade for on-chain funds. When funded, the operator converts the UTXO into a VTXO in the next batch. Protected by a timelock for unilateral recovery.

### Unilateral Exit
At any time, a user can exit to on-chain Bitcoin without the operator's cooperation by broadcasting the VTXO tree transaction. This is the security guarantee that makes Arkade non-custodial.

---

## Security

- The operator is **trusted for liveness only** — never for custody
- Users can always exit to on-chain Bitcoin unilaterally
- Private keys never leave your server
- The plugin uses POST redirects for private key display (never URL query params)
- All wallet state is stored in your own PostgreSQL database

**Never share your mnemonic or nsec with anyone, including Ark Labs.**

---

## Greenfield REST API

The plugin exposes a store-scoped REST API under `/api/v1/stores/{storeId}/arkade/*`. Endpoints are authenticated via BTCPay's standard Greenfield API key scheme and use the same permission policies as the rest of BTCPay (`btcpay.store.cansettings`, `btcpay.store.canmodifystoresettings`).

### Endpoints

- `GET /api/v1/stores/{storeId}/arkade/wallet` — current Arkade wallet config.
- `POST /api/v1/stores/{storeId}/arkade/wallet` — create or import a wallet.
- `PATCH /api/v1/stores/{storeId}/arkade/wallet/settings` — update destination, sub-dust, boarding.
- `DELETE /api/v1/stores/{storeId}/arkade/wallet` — unlink wallet from the store.
- `GET /api/v1/stores/{storeId}/arkade/balance` — available / locked / recoverable / boarding sats.
- `GET|POST /api/v1/stores/{storeId}/arkade/address` — read or mint a receive / boarding address.
- `POST /api/v1/stores/{storeId}/arkade/send` — send to an Ark address, BIP21 URI, or BOLT11 invoice.
- `POST /api/v1/stores/{storeId}/arkade/estimate-fees` — estimate fees for a prospective send (Arkade / Batch / Lightning).
- `POST /api/v1/stores/{storeId}/arkade/parse-destination` — classify a destination (Ark / BIP21 / BOLT11 / LNURL).
- `GET /api/v1/stores/{storeId}/arkade/vtxos` — list VTXOs.
- `GET /api/v1/stores/{storeId}/arkade/intents` — list pending batch intents.
- `DELETE /api/v1/stores/{storeId}/arkade/intents/{intentTxId}` — cancel an intent.
- `GET /api/v1/stores/{storeId}/arkade/contracts` — list address derivations.
- `GET /api/v1/stores/{storeId}/arkade/server-info` — Ark operator info.
- `GET /api/v1/stores/{storeId}/arkade/status` — overall service status.
- `GET /api/v1/stores/{storeId}/arkade/lightning-solver` — the Arkade swap solver this store trades Lightning corridors with.
- `POST /api/v1/stores/{storeId}/arkade/swaps/{swapId}/refresh` — re-read a swap from the chain and claim or refund it if due; reopens one its deadline closed.
- `POST /api/v1/stores/{storeId}/arkade/sync` — force a VTXO + boarding sync.

### Send example

```http
POST /api/v1/stores/{storeId}/arkade/send
Authorization: token <api-key>
Content-Type: application/json

{
  "destination": "ark1...",
  "amountSats": 25000,
  "inputOutpoints": ["abc...:0", "def...:1"]
}
```

- `amountSats` is required when the destination is a bare Ark address. For BIP21 URIs it overrides any embedded `amount`. For Lightning destinations it must be omitted (the BOLT11 invoice fixes the amount).
- `inputOutpoints` is optional. When provided, only the listed VTXOs are spent — automatic coin selection is bypassed. Must be omitted for Lightning destinations.

---

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for the full version history.

---

## Contributing

Pull requests are welcome. For significant changes, open an issue first to discuss.

- Branch naming: `your-name/short-description`
- Ensure `dotnet build NArk.sln` passes before submitting
- Add or update tests for new payment flows

---

## Links

- [Arkade](https://arkadeos.com) — the Ark protocol implementation
- [Ark Labs](https://arklabs.to) — the team building Arkade
- [BTCPay Server](https://btcpayserver.org) — the self-hosted payment processor

---

## License

[MIT](LICENSE) © Ark Labs
