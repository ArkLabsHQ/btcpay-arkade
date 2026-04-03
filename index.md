---
_layout: landing
---

# Arkade BTCPay Plugin

> Accept Bitcoin payments through [Arkade](https://arkadeos.com) — a self-custodial, off-chain Bitcoin Layer 2 — directly inside BTCPay Server.

**btcpay-arkade** is a BTCPay Server plugin that integrates [Arkade](https://arkadeos.com) as a payment method. It lets merchants accept instant, low-fee Bitcoin payments off-chain while retaining full self-custody — no Lightning node required, no custodian involved.

Payments are settled through **Virtual UTXOs (VTXOs)**, Arkade's off-chain Bitcoin outputs that are cryptographically anchored to real Bitcoin and can be unilaterally exited to the base chain at any time.

## Documentation

| | |
|---|---|
| **[Getting Started](docs/articles/getting-started.md)** | Install the plugin and configure your first store |
| **[Architecture](docs/articles/architecture.md)** | How the plugin is structured and how components interact |
| **[Payment Flows](docs/articles/payment-flows.md)** | Arkade Native, Lightning via Boltz, and Boarding Address |
| **[Ark Protocol](docs/articles/ark-concepts.md)** | VTXOs, batches, contracts, boarding, and unilateral exit |
| **[Plugin API Reference](api/index.md)** | Auto-generated API docs for the BTCPay plugin |
| **[NNark SDK Reference](sdk-api/index.md)** | Auto-generated API docs for the .NET Ark SDK |
| **[Development Guide](docs/articles/development.md)** | Build from source, run tests, contribute |

## Links

- [GitHub Repository](https://github.com/ArkLabsHQ/btcpay-arkade)
- [Arkade](https://arkadeos.com) — the Ark protocol implementation
- [BTCPay Server](https://btcpayserver.org) — the self-hosted payment processor
- [Changelog](https://github.com/ArkLabsHQ/btcpay-arkade/blob/master/CHANGELOG.md)
