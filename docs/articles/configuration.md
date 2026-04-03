# Configuration

This page documents all configuration options available in the Arkade BTCPay plugin.

## Store-Level Settings

These settings are configured per BTCPay store via **Store Settings > Arkade**.

### Connection

| Setting | Required | Description |
|---|---|---|
| Arkade Server URL | Yes | gRPC endpoint of your arkd instance (e.g., `https://arkd.yourdomain.com`) |
| Wallet Type | Yes | `HD Wallet` (BIP-39 mnemonic) or `SingleKey Wallet` (Nostr nsec) |
| Wallet Seed / Key | Yes | BIP-39 mnemonic phrase or Nostr `nsec` private key |

### Payment Methods

| Setting | Default | Description |
|---|---|---|
| Arkade Enabled | Disabled | Enable Arkade native payments on invoices |
| Lightning (Boltz) Enabled | Disabled | Enable Lightning payments via Boltz submarine swaps |

### Invoice Behavior

| Setting | Default | Description |
|---|---|---|
| Boarding Address | Enabled | Show boarding address (on-chain entry) on invoices |
| Boarding Minimum | 330 sats | Minimum invoice amount to display boarding address |
| Sub-dust Payments | Disabled | Accept payments below 330 sats |

### Wallet Automation

| Setting | Default | Description |
|---|---|---|
| Auto-sweep Address | Empty | If set, automatically forwards all received funds to this on-chain Bitcoin address |

## Payout Processor

The Arkade payout processor handles automated payouts via BTCPay's payout system. Configure it at **Store Settings > Payout Processors > Ark Payout Processor**.

| Setting | Default | Description |
|---|---|---|
| Interval | 1 hour | How often to process pending Arkade payouts |
| Fee Threshold | — | Maximum fee (in sats) to allow for a payout batch |

## Boltz Integration

When Lightning via Boltz is enabled, the plugin connects to a Boltz backend instance. The Boltz URL is configured alongside the Arkade server settings.

> [!NOTE]
> The plugin requires a self-hosted or trusted Boltz instance. While the swap protocol is trustless (HTLC-based), the Boltz API endpoint must be available for swap coordination.

## Plugin Dependency

The plugin requires **BTCPay Server v2.3.7** or later. Attempting to install on an older version will fail with a dependency error. This minimum version ensures compatibility with the .NET 10 runtime and API changes introduced in BTCPay Server 2.3.7.
