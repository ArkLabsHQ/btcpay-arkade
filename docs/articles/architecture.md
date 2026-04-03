# Architecture

The Arkade BTCPay plugin is structured as a standard BTCPay Server plugin with deep integration into the payment, payout, and wallet subsystems.

## High-Level Overview

```
BTCPay Server
└── Arkade Plugin
    ├── ArkController              # HTTP endpoints for store management
    ├── ArkContractInvoiceListener # Monitors contract state → updates invoice status
    ├── BoardingTransactionListener# Watches on-chain boarding UTXOs via NBXplorer
    ├── ArkadeSpendingService      # Sends payments (payouts, refunds)
    └── NNark (submodule)          # .NET Arkade SDK
        ├── NArk.Abstractions      # Interfaces and domain types
        ├── NArk.Core              # Wallet, VTXO logic, HD/SingleKey signers
        ├── NArk.Storage.EfCore    # PostgreSQL persistence (EF Core)
        └── NArk.Swaps             # Boltz submarine/reverse swap client
```

## Plugin Components

### ArkController

The main HTTP controller handling all store-facing UI endpoints: wallet setup, send wizard, VTXO management, contract inspection, swap monitoring, and admin pages. Uses BTCPay's cookie authentication and store authorization.

### ArkContractInvoiceListener

A background service that subscribes to VTXO state changes from the Ark SDK. When a VTXO matching an invoice's contract is detected, it updates the BTCPay invoice status accordingly. Handles both instant (off-chain) and boarding (on-chain confirmation required) payment flows.

### BoardingTransactionListener

Monitors on-chain transactions via NBXplorer for boarding address payments. When a customer sends Bitcoin to a Taproot boarding address, this listener detects the transaction and triggers the invoice payment flow once confirmed.

### ArkadeSpendingService

Handles outgoing payments: payout processing, manual sends from the wallet UI, and refunds. Constructs Ark intents with the appropriate contract derivation and submits them to the batch round.

## NNark SDK

The plugin delegates all Ark protocol operations to the [NNark SDK](https://github.com/arkade-os/dotnet-sdk), included as a Git submodule. The SDK provides:

- **Wallet management**: HD and SingleKey wallet implementations with BIP-44 derivation
- **VTXO lifecycle**: Tracking, spending, and syncing virtual UTXOs
- **Intent system**: Constructing and submitting batch payment intents
- **Contract derivation**: Taproot contract address generation per invoice
- **Swap integration**: Boltz submarine and reverse swap orchestration
- **Storage**: EF Core-based persistence for all Ark state

## Data Flow

### Incoming Payment

```
Customer → QR Code (BIP-21) → Arkade Native / Lightning / Boarding
                                    ↓
                          ArkContractInvoiceListener
                          BoardingTransactionListener
                                    ↓
                          BTCPay Invoice → Settled
```

### Outgoing Payment

```
Merchant → Send Wizard / Payout Processor
                    ↓
           ArkadeSpendingService
                    ↓
           NNark Intent → Batch Round → Committed
```

## Persistence

All plugin state is stored in BTCPay Server's existing PostgreSQL database using a dedicated schema (`BTCPayServer.Plugins.Ark`). EF Core migrations are bundled with the plugin and applied automatically on startup.

Key tables:

- **Wallets** — HD/SingleKey wallet configurations per store
- **VTXOs** — Virtual UTXO state, amounts, expiry, metadata
- **Contracts** — Taproot contracts with derivation indices
- **Intents** — Batch payment intents and their lifecycle
- **Swaps** — Boltz swap state and metadata
