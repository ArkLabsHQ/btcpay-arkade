# Arkade for BTCPay Server

> Accept Bitcoin payments through [Arkade](https://arkadeos.com) — a self-custodial, off-chain Bitcoin Layer 2 — directly inside BTCPay Server.

[![Version](https://img.shields.io/badge/version-2.0.4-blue)](CHANGELOG.md)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![BTCPay Plugin](https://img.shields.io/badge/BTCPay%20Server-Plugin-orange)](https://btcpayserver.org)

---

## What Is This?

**btcpay-arkade** is a BTCPay Server plugin that integrates [Arkade](https://arkadeos.com) as a payment method. It lets merchants accept instant, low-fee Bitcoin payments off-chain while retaining full self-custody — no Lightning node required, no custodian involved.

Payments are settled through **Virtual UTXOs (VTXOs)**, Arkade's off-chain Bitcoin outputs that are cryptographically anchored to real Bitcoin and can be unilaterally exited to the base chain at any time.

---

## Why Arkade?

| Feature | Lightning | Arkade |
|---|---|---|
| Self-custodial | ✅ | ✅ |
| Instant payments | ✅ | ✅ |
| No channel management | ❌ | ✅ |
| No inbound liquidity | ❌ | ✅ |
| Receive while offline | ❌ | ✅ |
| Unilateral on-chain exit | ❌ | ✅ |
| Bitcoin-only, no soft fork | ❌ | ✅ |

Arkade is complementary to Lightning, not a replacement. This plugin supports **both** natively — Lightning payments route through [Boltz](https://boltz.exchange) submarine swaps, giving you unified coverage across all payers.

---

## Payment Flows

### 1. Arkade Native
Direct VTXO-to-VTXO off-chain payments within the Arkade network. Instant settlement, zero routing fees. Payers need an Arkade-compatible wallet.

### 2. Lightning via Boltz
Payers with Lightning wallets pay a BOLT11 invoice. The plugin uses Boltz's trustless submarine swap to convert the Lightning payment into a VTXO in your Arkade wallet. No Lightning node needed on the merchant side.

### 3. Boarding Address
Payers send on-chain Bitcoin to a Taproot "boarding address." The Arkade operator batches this into the next round, converting the on-chain UTXO into a VTXO. If the operator is unresponsive, the payer can reclaim funds unilaterally after a timelock.

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
        ├── NArk.Storage.EfCore    # PostgreSQL persistence (EF Core 8)
        └── NArk.Swaps             # Boltz submarine/reverse swap client
```

The plugin persists all state (VTXOs, contracts, swaps, intents, wallets) in BTCPay's existing PostgreSQL database via EF Core migrations.

---

## Requirements

- **BTCPay Server** (self-hosted, any recent version)
- **PostgreSQL** (bundled with standard BTCPay deployments)
- **Arkade server (arkd)** v0.9.0 or later — accessible over gRPC from your BTCPay host
- **.NET 8** SDK (if building from source)

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
3. Optionally enable **Lightning (via Boltz)** if you have a Boltz instance configured

### 3. Store Settings

| Setting | Default | Description |
|---|---|---|
| Boarding Address | Enabled | Show boarding address on invoices (on-chain entry to Arkade) |
| Boarding Minimum | 330 sats | Minimum amount to display boarding address (dust threshold) |
| Sub-dust Payments | Disabled | Accept payments below 330 sats (no dust limit for VTXOs) |
| Auto-sweep Address | — | Forward all received funds to this on-chain address automatically |

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
- Boltz submarine swaps for incoming Lightning payments
- Boltz reverse swaps for outgoing Lightning payments
- Real-time swap lifecycle monitoring
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
- .NET 8 SDK
- Docker (for test environment)
- PostgreSQL

### Running Tests

After running `setup.sh`, start the local regtest environment (nigiri + arkd):
```bash
./start-env.sh
```

On Windows (via WSL):
```cmd
start-test-env.cmd
```

This spins up a regtest Bitcoin node, an Arkade server, and supporting services locally. Then run the E2E test suite:
```bash
dotnet test NArk.E2E.Tests/
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
│   └── btcpayserver/                   # BTCPay Server source (build dependency)
├── docs/                               # Internal design documents
├── setup.sh / setup.ps1               # First-time setup scripts
└── add-migration.sh / .ps1            # EF Core migration helpers
```

### Release Process

CI automatically creates a GitHub Release with the changelog body when a version tag is pushed:
```bash
git tag v2.0.4
git push origin v2.0.4
```

---

## Ark Protocol Concepts

### VTXOs (Virtual UTXOs)
Off-chain Bitcoin outputs secured via collaborative (user + operator) and unilateral (timelocked) Taproot spending paths. VTXOs are the atomic unit of value in Arkade. The operator can never steal them — the unilateral exit path is always available.

### Rounds & Commitment Transactions
Periodically, the Arkade operator batches pending VTXOs into an on-chain **commitment transaction**, anchoring off-chain state to Bitcoin. This is how off-chain payments get Bitcoin-level finality.

### Contracts
Payment flows are modeled as Taproot contracts derived from a wallet descriptor and derivation index. Each invoice gets a unique contract address.

### Boarding Addresses
Taproot addresses that serve as the entry point into Arkade for on-chain funds. When funded, the operator converts the UTXO into a VTXO in the next round. Protected by a timelock for unilateral recovery.

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
- [Boltz Exchange](https://boltz.exchange) — trustless Lightning ↔ on-chain swaps

---

## License

[MIT](LICENSE) © Ark Labs
