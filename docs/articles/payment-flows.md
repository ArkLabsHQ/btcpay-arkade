# Payment Flows

The Arkade plugin supports three payment methods, all presented simultaneously in a unified BIP-21 QR code. The customer's wallet automatically picks the best method it supports.

## 1. Arkade Native

Direct VTXO-to-VTXO off-chain payments within the Arkade network.

- **Speed**: Instant (within the next batch round, typically ~10 seconds)
- **Fees**: Zero routing fees
- **Requirement**: Payer needs an Arkade-compatible wallet
- **Flow**: Payer scans QR → wallet sends VTXO to merchant's contract address → `ArkContractInvoiceListener` detects the VTXO → invoice marked as settled

This is the most efficient payment method. The merchant receives a VTXO that is immediately spendable in the next batch round.

## 2. Lightning via Boltz

Payers with Lightning wallets pay a BOLT11 invoice. The plugin uses [Boltz](https://boltz.exchange) trustless submarine swaps to convert the Lightning payment into a VTXO.

- **Speed**: Near-instant (Lightning payment + swap execution)
- **Fees**: Boltz swap fee (typically ~0.1-0.5%)
- **Requirement**: No Lightning node needed on the merchant side
- **Flow**: Payer pays BOLT11 invoice → Boltz receives Lightning → Boltz creates VTXO for merchant → invoice settled

> [!NOTE]
> The merchant never touches Lightning directly. Boltz handles the conversion trustlessly using Hash Time-Locked Contracts (HTLCs), so no custodial risk is introduced.

## 3. Boarding Address

Payers send on-chain Bitcoin to a Taproot "boarding address."

- **Speed**: Requires 1 on-chain confirmation, then batch inclusion
- **Fees**: Standard Bitcoin on-chain fee (paid by sender)
- **Requirement**: Any Bitcoin wallet can pay
- **Flow**: Payer sends BTC to Taproot address → `BoardingTransactionListener` detects the tx → invoice shows "Processing" → after 1 confirmation, the operator converts it to a VTXO in the next batch → invoice settled

The boarding address is a Taproot output with two spending paths:

1. **Cooperative**: The Arkade operator and the user jointly sign to convert the UTXO into a VTXO (normal flow)
2. **Unilateral**: After a timelock expires, the user can reclaim the funds on-chain without the operator (safety net)

## Unified BIP-21 QR Code

The checkout page encodes all applicable payment methods in a single [BIP-21](https://github.com/bitcoin/bips/blob/master/bip-0021.mediawiki) URI:

```
bitcoin:<boarding-address>?amount=0.001&ark=<ark-address>&lightning=<bolt11>
```

Any wallet scans the same QR code and picks the method it supports — Arkade-native wallets use the `ark=` parameter, Lightning wallets use `lightning=`, and on-chain wallets fall back to the base Bitcoin address.

## Invoice Lifecycle

| State | Meaning |
|---|---|
| **New** | Invoice created, waiting for payment |
| **Processing** | Boarding payment detected, awaiting confirmation |
| **Settled** | Payment received as VTXO, invoice complete |
| **Expired** | No payment received within the timeout |
