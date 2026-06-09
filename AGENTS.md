# NArk Repo Guide
A BTCPayServer plugin that lets merchants accept Bitcoin through Arkade — a
self-custodial, offchain protocol built directly on Bitcoin. The plugin and its
test project target **.NET 10**; the NNark SDK libraries (in the submodule)
target **.NET 8**.

## Structure
- `BTCPayServer.Plugins.ArkPayServer`: the plugin. Payment handlers, store UI
  (Razor), controllers, and EF Core (PostgreSQL) persistence; consumes the
  NNark SDK and BTCPayServer's payment pipeline.
- `submodules/NNark`: the Arkade .NET SDK (GitHub `arkade-os/dotnet-sdk`).
  Key projects: `NArk.Core` (contracts/scripts, gRPC + REST transport),
  `NArk.Abstractions` (interfaces/base types), `NArk.Swaps` (Boltz swap
  providers + management), `NArk.Storage.EfCore` (EF persistence).
- `submodules/btcpayserver`: BTCPayServer source, pulled as a submodule.
- `NArk.E2E.Tests`: Playwright + BTCPayServer `ServerTester` end-to-end suite.
  Not part of `NArk.sln` (runs in the `e2e` CI workflow, not the `build` one).
- `NArk.sln`: the solution — the plugin plus the BTCPayServer and NNark
  library projects it references.

## Setup
Run `./setup.sh` (or `./setup.ps1` on Windows) after cloning: it initialises
submodules, restores .NET workloads, registers the plugin with the dev server
via `DEBUG_PLUGINS` in `submodules/btcpayserver/BTCPayServer/appsettings.dev.json`,
and publishes the plugin (NNark dependencies included). Use
`./add-migration.sh <Name>` to add EF Core migrations, and `start-test-env.cmd`
to bring up the local regtest/Arkade test environment.

## Agent Guidance
- Follow standard .NET naming conventions.
- Ensure the solution builds successfully before committing.
- Keep this file up to date when repo structure changes.

## Version Bumps
- When asked to bump the version, find the diff since the last version bump commit (look for commits like "bump", "v2.0.x", or version changes in the csproj).
- Include all SDK (NNark submodule) changes in the diff — check `git -C submodules/NNark log` for commits since the last submodule pointer update.
- Generate a `CHANGELOG.md` at the repo root with the new version entry, summarizing:
  - New features
  - Bug fixes
  - Breaking changes (if any)
  - SDK/dependency updates
- Use the existing CHANGELOG.md format if one exists, otherwise start with `# Changelog` and `## [version] - YYYY-MM-DD` sections.
- ALWAYS update the CHANGELOG.md when bumping the version — never bump without a changelog entry.

## Conceptual Overview
The plugin enables merchants to accept Bitcoin payments through Ark, using
Virtual UTXOs (VTXOs) for instant, low-cost transactions that can later be
anchored onchain for finality.

## Supported Flows
The plugin supports these key flows:

- **Ark-native**: direct offchain VTXO-to-VTXO payments within the Ark network.
- **Boltz-Ark**: trustless Lightning↔Ark swaps using BOLT11 invoices via Boltz
  (submarine and reverse swaps).
- **Boarding Address Flow**: users enter the Ark system by funding a Taproot
  "boarding address," which is converted into a VTXO with help from the Arkade
  Operator. If the Operator is unresponsive, users can reclaim funds
  unilaterally after a timelock.
- **Arkade asset acceptance**: a store can settle invoices in a
  merchant-declared Arkade asset at a merchant-declared rate.

Each flow is implemented as a type of contract, created using a wallet defined
by a Miniscript descriptor and an address derivation index.

## Ark Concepts

- **VTXOs**: Offchain Bitcoin outputs secured via collaborative (user + operator) and unilateral (timelocked) Taproot paths. These are the basic payment units in Ark.
- **Contracts**: Payment flows (Ark-native and Boltz) are modeled as Taproot contracts generated from a descriptor and derivation index. Each contract results in a unique payment address.
- **Boarding Addresses**: Onchain Taproot addresses that act as trust-minimized entry points to Ark. When funded, they allow the plugin to request the Arkade Operator to convert the UTXO into a VTXO.
- **Commitment Transactions**: Onchain transactions created by the operator that anchor offchain VTXO state into Bitcoin, securing it with Bitcoin-level finality.

## Arkade OS Integration
Arkade extends Ark with programmability and verifiability. It introduces:

- **Arkade Script**: A scripting layer that expands Bitcoin Script for more expressive offchain contracts.
- **Arkade Signer**: A TEE-secured module that co-signs VTXO transactions and prevents double-spends through hardware-enforced integrity.

The plugin integrates with Arkade to:

- Generate boarding addresses and contracts
- Detect onchain deposits and initiate VTXO creation
- Subscribe to contract activity from the operator
- Anchor contracts onchain when requested or required

## Design Considerations

- Wallets use Miniscript descriptors and must manage address derivation state safely.
- All contracts must enforce a secure unilateral exit path for the user.
- Boarding addresses must be monitored and resolved into VTXOs.
- Payment detection is event-driven, based on Arkade Operator subscriptions.
- Contracts may remain offchain until anchored, enabling both instant and deferred finality models.

## Problem Domain Summary
This plugin bridges Bitcoin-native offchain protocols with BTCPayServer's
payment system. It manages:

- Onboarding via boarding addresses
- Contract generation and tracking
- Offchain event monitoring through Arkade
- Invoice status updates based on offchain state transitions
