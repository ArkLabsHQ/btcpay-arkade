# Getting Started

This guide walks you through installing the Arkade plugin for BTCPay Server and configuring your first store.

## Requirements

- **BTCPay Server** v2.3.7 or later (self-hosted)
- **PostgreSQL** (bundled with standard BTCPay deployments)
- **Arkade server (arkd)** v0.9.0 or later — accessible over gRPC from your BTCPay host

> [!WARNING]
> **Alpha software.** This plugin is actively developed and not yet recommended for high-value production deployments. Always maintain a backup of your seed phrase.

## Installation

### Via BTCPay Plugin Manager (Recommended)

1. Open your BTCPay Server instance
2. Go to **Server Settings > Plugins**
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

## Store Configuration

### 1. Connect to Arkade

1. Navigate to your BTCPay store > **Settings > Arkade**
2. Enter your **Arkade server URL** (e.g., `https://arkd.yourdomain.com`)
3. Import your wallet:
   - **HD Wallet**: paste a BIP-39 mnemonic (12 or 24 words)
   - **SingleKey Wallet**: paste a Nostr `nsec` private key

See [Wallet Types](wallet-types.md) for details on choosing the right wallet type.

### 2. Enable Payment Methods

1. Go to **Store Settings > Payment Methods**
2. Enable **Arkade** as a payment method
3. Optionally enable **Lightning (via Boltz)** if you have a Boltz instance configured

### 3. Store Settings

| Setting | Default | Description |
|---|---|---|
| Boarding Address | Enabled | Show boarding address on invoices (on-chain entry to Arkade) |
| Boarding Minimum | 330 sats | Minimum amount to display boarding address (dust threshold) |
| Sub-dust Payments | Disabled | Accept payments below 330 sats (no dust limit for VTXOs) |
| Auto-sweep Address | — | Forward all received funds to this on-chain address automatically |

See [Configuration](configuration.md) for the full settings reference.

## Next Steps

- Learn about [Payment Flows](payment-flows.md) to understand how customers pay
- Explore [Ark Protocol Concepts](ark-concepts.md) for the underlying mechanics
- Read the [Development Guide](development.md) if you want to contribute
