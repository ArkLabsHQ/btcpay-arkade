# Mass Action Table Column Redesign

## Overview

This document describes the VTXO table column restructuring that was implemented. The original goal was to create a TagHelper-based reusable component, but due to technical limitations with Razor template rendering in TagHelpers, we opted for inline table markup with consistent column structure.

## Problem

The VTXO table columns needed restructuring:
- TXID and Output were separate columns - should be a single Outpoint column
- Expiry date was not visible
- Status and Spendable were separate columns - should be consolidated (with separate badges)

## Solution Implemented

### VTXO Column Changes

**Before:** Seen | TXID | Output | Amount | Status | Spendable

**After:** Seen | Outpoint | Amount | Expiry | Status

Where Status shows multiple badges:
- Spent / Recoverable / Unspent (mutually exclusive)
- Spendable badge shown additionally when applicable (for non-spent VTXOs)

### Files Modified

- `Views/Ark/Vtxos.cshtml` - Main VTXO list with new 5-column layout
- `Views/Ark/_VtxoTable.cshtml` - VTXO sublist partial with Expiry column added

### Files Deleted

- `Views/Ark/_MassActionTableWrapper.cshtml` - Obsolete wrapper partial

## Technical Notes

### Why TagHelper Approach Was Abandoned

The original plan was to create TagHelper components (`<mass-action-table>`, `<row-template>`, etc.) for reusable table rendering. This approach was abandoned because:

1. **Template Re-rendering Limitation**: TagHelpers cannot re-render Razor content with different context per iteration. The `row-template` would need to be executed once per item, but TagHelpers don't support this pattern natively.

2. **Alternative Approaches Considered**:
   - `@<text>` templated delegates produce `Func<object, HelperResult>` which is incompatible with `IHtmlContent`
   - ViewComponents require separate .cshtml files and don't support inline templates
   - Partial views with configuration objects work but require passing templates as pre-rendered IHtmlContent, which defeats the purpose

3. **Decision**: Keep inline table markup in views. The duplication is acceptable given the complexity of alternatives.

### Consistency Maintained

- Both `Vtxos.cshtml` and `_VtxoTable.cshtml` now use:
  - `vtxo.IsSpent()` method for spent detection
  - Same column order: Outpoint | Amount | Expiry | Status
  - Same badge styling for status display

### Spendable Badge Logic

The Spendable badge is shown as a separate badge alongside the status badge:
- Only shown when the VTXO is NOT spent
- Can appear with both "Unspent" and "Recoverable" status badges
- Uses `text-bg-info` styling

This addresses the user requirement: "one can have a recoverable yet spendable vtxo"
