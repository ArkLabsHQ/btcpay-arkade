# Ark Protocol Concepts

This page explains the core concepts behind the Ark protocol as they apply to the Arkade BTCPay plugin. For the full Ark protocol specification, see [ark-protocol.org](https://ark-protocol.org).

## VTXOs (Virtual UTXOs)

VTXOs are the atomic unit of value in Arkade. They are off-chain Bitcoin outputs secured via collaborative (user + operator) and unilateral (timelocked) Taproot spending paths.

Key properties:

- **Self-custodial**: The operator can never steal VTXOs — the unilateral exit path is always available
- **Instant transfers**: VTXO-to-VTXO payments within Arkade settle in the next batch round (~10 seconds)
- **Bitcoin-backed**: Every VTXO is anchored to a real on-chain Bitcoin output via the commitment transaction tree
- **Expiring**: VTXOs have a lifetime (set by the operator). Before expiry, they must be "refreshed" into a new batch — the plugin handles this automatically

## Batches and Commitment Transactions

Periodically (typically every ~10 seconds), the Arkade operator runs a **batch round**:

1. Collects all pending payment intents from users
2. Constructs a new VTXO tree
3. Creates and broadcasts an on-chain **commitment transaction** that anchors the entire tree

This commitment transaction is how off-chain payments get Bitcoin-level finality. Each batch produces one on-chain transaction regardless of how many payments it contains.

## Contracts

In the plugin, a **contract** is a Taproot address derived from the wallet's descriptor and a derivation index. Each BTCPay invoice gets a unique contract, allowing the plugin to track which payments correspond to which invoices.

Contracts are the link between:

- The BTCPay invoice system
- The Ark protocol's VTXO addressing
- The wallet's key derivation

## Intents

An **intent** is a request to include a payment in the next batch round. When you send funds from the plugin's wallet, it:

1. Constructs an intent specifying the recipient, amount, and source VTXOs
2. Submits the intent to the Arkade operator
3. Waits for the next batch round to include it
4. Signs the batch transaction collaboratively with the operator

Intents can include multiple outputs (e.g., payment + change), making batch payments efficient.

## Boarding Addresses

Boarding addresses are Taproot outputs that serve as the **on-ramp from on-chain Bitcoin into Arkade**.

A boarding address has two spending paths:

- **Cooperative path**: The operator and user sign together to convert the UTXO into a VTXO in the next batch. This is the normal, fast path.
- **Unilateral path**: After a timelock expires, the user can reclaim the funds on-chain without the operator's cooperation. This is the safety net.

When a customer pays an invoice using the boarding address, the `BoardingTransactionListener` detects the on-chain transaction, and after 1 confirmation, the operator includes it in a batch.

## Unilateral Exit

At any time, a user can exit to on-chain Bitcoin **without the operator's cooperation** by broadcasting the VTXO tree transaction. This is the fundamental security guarantee that makes Arkade non-custodial.

The process:

1. User broadcasts the relevant branch of the commitment transaction tree
2. After a timelock, the user can claim their output on-chain
3. No operator involvement required

> [!NOTE]
> Unilateral exit is the "nuclear option" — it's slower and costs more in on-chain fees than cooperative spending, but it guarantees that funds are never locked or lost even if the operator goes offline permanently.

## Swaps (Lightning Integration)

The plugin integrates with [Boltz Exchange](https://boltz.exchange) for trustless Lightning swaps:

- **Submarine swap** (Lightning → Ark): Customer pays a Lightning invoice, Boltz converts it to a VTXO for the merchant
- **Reverse swap** (Ark → Lightning): Merchant sends a VTXO to Boltz, Boltz pays a Lightning invoice on their behalf

These swaps use Hash Time-Locked Contracts (HTLCs) and are trustless — neither party can steal funds during the swap process.
