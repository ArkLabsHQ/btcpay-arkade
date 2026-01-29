# Unified Send Wizard Design

## Overview

Replace SpendOverview and IntentBuilder with a single, unified Send Wizard that adapts based on entry point and provides progressive disclosure for power users.

## Design Decisions

- **Progressive disclosure**: Simple path for merchants, full control for power users
- **Destination-first**: User pastes address/invoice, system auto-detects spend type and selects coins
- **Fully automatic coin selection**: Smart defaults based on destination type and available coins
- **Expandable sections**: All wizard sections visible, active section expanded, others collapsed with summary

## Entry Points

| Source | Query Params | Wizard Behavior |
|--------|--------------|-----------------|
| Nav "Send" button | none | Destination field focused, auto coin selection |
| VTXOs page mass action | `?vtxos=txid:vout,...` | Coins pre-selected, skip to outputs step, coin summary expandable |
| Payout handler | `?destinations=addr:amt,...` | Destinations pre-filled, user confirms |
| BIP21/LNURL link | `?destination=bip21://...` | Pre-fills destination, proceeds normally |

## Spend Type Detection

The destination type combined with available coin types determines the optimal spend type:

| Destination | Available Coins | Spend Type |
|-------------|-----------------|------------|
| Ark address | Non-recoverable only | Offchain (preferred) |
| Ark address | Recoverable only | Batch |
| Ark address | Mixed | Offchain if sufficient, else batch |
| Bitcoin address | Any | Batch (onchain output) |
| Lightning invoice | Non-recoverable only | Swap |
| Lightning invoice | Recoverable only | **Error** (can't swap recoverable) |
| Empty (consolidation) | Any | Batch to self |

**Key rule**: Offchain is always preferred for Ark addresses when possible. Recoverable coins can only be spent via batch.

## UI Layout

Single-page wizard with collapsible sections:

```
┌─────────────────────────────────────────────────────┐
│  Send                                               │
├─────────────────────────────────────────────────────┤
│  ▼ Destination                          [expanded]  │
│  ┌───────────────────────────────────────────────┐  │
│  │ [Paste address, invoice, or BIP21...]        │  │
│  │                                               │  │
│  │ + Add another output                          │  │
│  └───────────────────────────────────────────────┘  │
│  Detected: Ark address → Offchain transfer          │
│                                                     │
├─────────────────────────────────────────────────────┤
│  ▶ Coins (3 selected · 0.00500000 BTC)  [collapsed] │
│    [Edit selection]                                 │
│                                                     │
├─────────────────────────────────────────────────────┤
│  ▶ Review                               [collapsed] │
│                                                     │
├─────────────────────────────────────────────────────┤
│  Fee: ~250 sats                    [Send] [Cancel]  │
└─────────────────────────────────────────────────────┘
```

### Destination Section

- Text input supporting: Ark addresses, Bitcoin addresses, BIP21 URIs, BOLT11 invoices
- Optional amount field (auto-filled for single output sending all)
- "Add another output" button (disabled for Lightning - single output only)
- Shows detected destination type badge

### Coins Section (Collapsed by Default)

When collapsed:
- Summary: "3 coins · 0.00500000 BTC"
- "Edit selection" link

When expanded:
- Table of selected VTXOs (outpoint, amount, expiry, status)
- Checkboxes to deselect coins
- "Select more" button opens inline picker with available (spendable) coins
- Shows coin selection mode: "Auto" or "Manual"

### Review Section (Collapsed by Default)

When collapsed:
- Summary: "Sending 0.005 BTC via Offchain"

When expanded:
- Full summary: destination, amount, spend type badge
- Fee breakdown (expandable for LN: service fee + miner fee)
- "Show details" link → inputs list, outputs list, change info

## State Management

### Client-Side State (JavaScript)

```javascript
WizardState = {
  destinations: [{ address: "", amount: null }],
  selectedVtxos: [],        // OutPoint strings
  coinSelectionMode: "auto" | "manual",
  detectedSpendType: null,  // "offchain" | "batch" | "swap"
  feeEstimate: null,
  errors: []
}
```

### Key Interactions

1. **Destination change** (debounced 300ms):
   - Parse destination type (Ark/BTC/LN)
   - If `coinSelectionMode === "auto"`: call `/api/suggest-coins`
   - Call `/api/estimate-fees` with current state
   - Update `detectedSpendType`

2. **Coin selection change**:
   - Switch `coinSelectionMode` to `"manual"`
   - Validate coins compatible with destination
   - Re-estimate fees

3. **Add/remove output**:
   - LN detected? Disable "Add output"
   - Re-estimate fees

### API Endpoints

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/suggest-coins` | POST | Given destination type + amount, return optimal coin selection |
| `/api/estimate-fees` | POST | Given coins + outputs, return fee breakdown |
| `/api/validate-spend` | POST | Pre-flight check before submission |
| `/send` | POST | Execute the spend |

### Server-Side ViewModel

```csharp
public class SendWizardViewModel
{
    // Query param inputs
    public string? VtxoOutpoints { get; set; }
    public string? Destinations { get; set; }

    // Hydrated data (AvailableVtxos = spendable coins only)
    public List<ArkVtxo> AvailableVtxos { get; set; }
    public List<ArkVtxo> SelectedVtxos { get; set; }
    public List<OutputViewModel> Outputs { get; set; }
    public SpendType DetectedSpendType { get; set; }
    public FeeEstimate? Fee { get; set; }
}

public enum SpendType
{
    Offchain,  // Direct VTXO transfer (Ark to Ark, non-recoverable)
    Batch,     // Join Ark batch (onchain output or recoverable coins)
    Swap       // Lightning swap via Boltz
}
```

## Error Handling

### Inline Validation Errors

| Error | Location | Message |
|-------|----------|---------|
| Invalid address | Destination field | "Invalid address format" |
| LN + recoverable coins | Coins section | "Lightning requires non-recoverable coins. Remove recoverable coins or choose different destination." |
| Insufficient funds | Coins section | "Need 0.006 BTC but only 0.004 BTC selected" |
| LN + multiple outputs | Add output button | Disabled, tooltip: "Lightning supports single output only" |
| Expired invoice | Destination field | "Invoice expired" |
| Amount exceeds Boltz limits | Fee section | "Amount exceeds Lightning swap limit (X BTC)" |

### Edge Cases

1. **No spendable coins**: Show balance widget with "No spendable coins available", disable form
2. **Coin spent while editing**: On submit, validate coins still exist. Show error + refresh available coins
3. **Manual selection incompatible**: Warning banner with guidance to fix
4. **Pre-selected coins invalid**: Filter out invalid, show notice about unavailable coins

## Migration

### Deprecated Pages
- `SpendOverview.cshtml` → Redirect to `/send`
- `IntentBuilder.cshtml` → Redirect to `/send`

### Payout Handler Integration
The `ArkadePayoutHandler` currently uses SpendOverview. Update to redirect to `/send?destinations=...` with pre-filled destinations.

### URL Backward Compatibility
- `/spend?vtxoOutpoints=...` → Redirect to `/send?vtxos=...`
- Keep query param parsing compatible

## Implementation Steps

1. Create `SendWizardViewModel` and related DTOs
2. Implement API endpoints (`suggest-coins`, `estimate-fees`, `validate-spend`)
3. Create `Send.cshtml` view with collapsible sections
4. Implement client-side JavaScript state management
5. Wire up destination parsing and spend type detection
6. Implement coin selection UI (collapsed summary + expanded table)
7. Implement review section with expandable details
8. Update VTXOs page mass action to redirect to new wizard
9. Update payout handler to use new wizard
10. Add redirects from old pages
11. Remove SpendOverview and IntentBuilder after validation

## Files to Create/Modify

### New Files
- `Views/Ark/Send.cshtml` - Main wizard view
- `Models/SendWizardViewModel.cs` - ViewModel
- `Models/Api/SuggestCoinsRequest.cs` - API request DTO
- `Models/Api/SuggestCoinsResponse.cs` - API response DTO

### Modified Files
- `Controllers/ArkController.cs` - Add new endpoints, redirects
- `Views/Ark/Vtxos.cshtml` - Update mass action redirect
- `Payouts/ArkPayoutHandler.cs` - Update to use new wizard

### Deprecated Files (remove after migration)
- `Views/Ark/SpendOverview.cshtml`
- `Views/Ark/IntentBuilder.cshtml`
- `Models/SpendOverviewViewModel.cs`
- `Models/IntentBuilderViewModel.cs`
