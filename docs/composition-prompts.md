# Client-composed EVM settlement

EVM settlement is opt-in per store and per source rail: Arkade, Lightning, or
Bitcoin onchain. A merchant first imports an Arkade wallet, including a
watch-only Taproot account descriptor, then configures the EVM asset,
destination, solver policy, protected RPC endpoint, gas payer, and fee caps.

The plugin composes ordinary solver quotes on the client. A solver is never
asked to understand or execute the complete route:

1. The plugin creates a fresh preimage `P`, hash `H`, and outgoing
   Arkade-to-EVM RFQ. The returned Arkade covenant is lock `L`.
2. For Lightning or onchain input, it creates a second ordinary exact-output
   RFQ with the same `H`. Its non-interactive claim pays exactly into `L`; the
   ingress covenant is lock `M`. Direct Arkade input pays `L` itself.
3. After the customer pays, the plugin or covclaimd claims `M` into `L` without
   the merchant's Arkade signing key. Revealing `P` lets the outgoing solver
   fund the EVM HTLC.
4. The plugin verifies the allowed swap contract, token, recipient, exact ERC20
   amount, timeout, confirmations, and block age before `claimFor`.
5. Only the verified ERC20 claim marks the BTCPay payment settled. Funding or
   claiming an ingress leg never settles the invoice.

Each payment prompt and renewal owns an independent route, `H`, and RFQ IDs.
The incoming quote's spread is recorded as that payment method's fee, so the
customer destination and exact amount always match the selected route. An old
prompt remains independently settleable after a newer prompt is generated.

## Custody and recovery

Quote, funding, proof, and lifecycle state live in the SDK's intent storage,
which already persists both legs and the route preimage `P`. The plugin keeps
only a store-scoped routing index (invoice to SDK swap ids) plus the BTCPay
prompt and payment records; it duplicates no swap state. `P`, wallet
descriptors, RPC credentials, and gas keys are excluded from route APIs and
logs. Retries resume from the persisted SDK intents, so a crash cannot silently
create a different quote for an indexed route.

A watch-only merchant wallet is sufficient for the composed path: emulator
non-interactive claims move `M` to `L`, and the same path refunds a funded `L`
after its deadline. Existing funded routes use their persisted route policy for
recovery even if the merchant later disables or changes new-route settings.
Current protected RPC and gas credentials are still required to complete an
EVM claim.

Route issuance requires PostgreSQL advisory locks. The API reports
`cross-process-execution-lock-unavailable` and creates no payable prompt when a
safe lock backend is unavailable. Existing indexed routes are still advanced
for recovery.

## Greenfield API

Wallet setup accepts a public descriptor explicitly:

```json
{
  "mode": "WatchOnly",
  "wallet": "tr([fingerprint/86'/0'/0']xpub.../0/*)",
  "enableLightning": true
}
```

`POST /api/v1/stores/{storeId}/arkade/wallet` stores no signing material and
starts SDK wallet restoration/scanning. EVM configuration and route operations
are exposed under:

- `GET|PUT /api/v1/stores/{storeId}/arkade/evm-settlement`
- `GET /api/v1/stores/{storeId}/arkade/evm-settlement/capabilities`
- `GET /api/v1/stores/{storeId}/arkade/evm-settlement/routes`
- `GET /api/v1/stores/{storeId}/arkade/evm-settlement/routes/{routeId}`
- `POST /api/v1/stores/{storeId}/arkade/evm-settlement/invoices/{invoiceId}/onchain-prompt`

BTCPay's normal invoice activation automatically creates Arkade, Lightning, and
onchain composed prompts for enabled rails. The manual onchain endpoint is an
operational entry point for a known invoice; it does not bypass the same store,
amount, quote, lock, or settlement checks.
