# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
# Initial setup (pulls submodules, restores workloads, publishes plugin)
./setup.sh        # Linux/macOS
./setup.ps1       # Windows

# Build solution
dotnet build

# Run tests
dotnet test

# Run a single test
dotnet test --filter "FullyQualifiedName~TestMethodName"
dotnet test NArk.Tests --filter "ClassName.TestMethodName"

# Add EF Core migration
./add-migration.sh <MigrationName>
./add-migration.ps1 <MigrationName>
```

## Development Environment

```bash
# Start local dev environment (nigiri + ark stack)
./start-env.sh
./start-env.sh --clean  # Clean restart with fresh volumes

# Services available after startup:
# - Ark daemon: http://localhost:7070
# - Boltz API: http://localhost:9001
# - CORS proxy: http://localhost:9069
# - Chopsticks (Bitcoin explorer): http://localhost:3000
```

## Architecture

This is a BTCPayServer plugin enabling Ark protocol payments. It targets .NET 8.

### Projects

- **NArk**: Core library for Ark taproot contracts, scripts, and gRPC client code generated from `Protos/ark/v1/*.proto`. Contains Boltz REST client at `NArk/Boltz/`.

- **BTCPayServer.Plugins.ArkPayServer**: The BTCPayServer plugin. Uses EF Core with PostgreSQL (schema: `BTCPayServer.Plugins.Ark`). Entry point is `ArkPlugin.cs` which registers all services.

- **NArk.Tests**: xUnit tests for the NArk library.

- **submodules/btcpayserver**: BTCPayServer source as a git submodule.

### Key Plugin Components

- `ArkPlugin.cs`: Plugin entry point, registers DI services, configures gRPC clients for Ark/Indexer services
- `ArkWalletService`: Hosted service managing wallet lifecycle
- `ArkVtxoSynchronizationService`: Syncs VTXO state from Ark operator
- `ArkIntentService`: Manages transaction intents for batching
- `ArkContractInvoiceListener`: Monitors contracts for payment detection
- `BoltzService`: Lightning-to-Ark swap integration via Boltz API

### Data Layer

EF Core context: `ArkPluginDbContext` with entities:
- `ArkWallet`: Store wallet configuration
- `VTXO`: Virtual transaction outputs
- `ArkWalletContract`: Payment contracts linked to wallets
- `ArkSwap`: Boltz swap records
- `ArkIntent`: Batch transaction intents

### Payment Flow

1. Payment contracts are generated from Miniscript descriptors with address derivation
2. Contract addresses are monitored via Ark operator subscriptions
3. Payments can be: Ark-native (VTXO-to-VTXO), Boltz-Ark (Lightning via BOLT11), or boarding addresses
4. VTXOs are tracked and can be spent in Ark batches or anchored onchain

### Configuration

Plugin reads `ark.json` from BTCPayServer data directory. Falls back to network-specific defaults:
- Mainnet: `https://arkade.computer`, `https://api.ark.boltz.exchange/`
- Regtest: `http://localhost:7070`, `http://localhost:9001/`

## Docker Compose

`docker-compose.ark.yml` defines the local Ark development stack:
- `boltz-lnd` / `lnd`: Lightning nodes for swap testing
- `boltz`: Boltz exchange service for Lightning-Ark swaps
- `boltz-fulmine`: Ark wallet for Boltz
- `nginx-boltz`: CORS proxy for Boltz API (port 9069)

## Code Conventions

- Follow standard .NET naming conventions
- Ensure solution builds before committing
- gRPC clients are auto-generated from proto files during build
