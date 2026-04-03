# Wallet Types

The Arkade plugin supports two wallet types, each suited to different use cases.

## HD Wallet (BIP-39 Mnemonic)

The recommended wallet type for merchants.

- **Key derivation**: Full hierarchical deterministic derivation (BIP-44 style)
- **Address generation**: Unique contract address per invoice
- **Boarding support**: Yes — requires HD derivation for boarding address generation
- **Import**: 12 or 24-word BIP-39 mnemonic phrase

### When to Use

- Production merchant deployments
- When you need boarding address payments (on-chain entry to Arkade)
- When you want unique addresses per invoice for privacy
- When you already have a BIP-39 mnemonic from another wallet

### Setup

1. Navigate to **Store Settings > Arkade**
2. Enter your Arkade server URL
3. Select **HD Wallet**
4. Paste your 12 or 24-word mnemonic

> [!IMPORTANT]
> Store your mnemonic securely offline. It is the only way to recover your funds if the server is lost.

## SingleKey Wallet (Nostr nsec)

A simpler wallet type for lightweight deployments.

- **Key derivation**: Single static key — all contracts derive from one keypair
- **Address generation**: Shared contract address across invoices
- **Boarding support**: No — boarding addresses require HD derivation
- **Import**: Nostr `nsec` private key (bech32-encoded)

### When to Use

- Testing and development
- Lightweight or temporary deployments
- When you already have a Nostr identity (`nsec`) you want to reuse
- When boarding address support is not needed

### Setup

1. Navigate to **Store Settings > Arkade**
2. Enter your Arkade server URL
3. Select **SingleKey Wallet**
4. Paste your `nsec` private key

## Comparison

| Feature | HD Wallet | SingleKey Wallet |
|---|---|---|
| Key derivation | BIP-44 hierarchical | Single static key |
| Unique addresses per invoice | Yes | No |
| Boarding address support | Yes | No |
| Import method | BIP-39 mnemonic | Nostr `nsec` |
| Recommended for | Production merchants | Testing / lightweight |
