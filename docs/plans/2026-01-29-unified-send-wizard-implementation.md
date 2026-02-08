# Unified Send Wizard Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace SpendOverview and IntentBuilder with a single, unified Send Wizard featuring progressive disclosure for both merchants and power users.

**Architecture:** Destination-first flow with automatic spend type detection and coin selection. Single-page wizard with collapsible sections (Destination → Coins → Review). State managed client-side with server API calls for validation and fee estimation.

**Tech Stack:** ASP.NET Core MVC, Razor Views, vanilla JavaScript (IIFE pattern), BTCPayServer plugin patterns.

---

## Task 1: Create SendWizardViewModel

**Files:**
- Create: `Models/SendWizardViewModel.cs`

**Step 1: Write the ViewModel**

```csharp
using NArk.Abstractions.VTXOs;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

/// <summary>
/// ViewModel for the unified Send Wizard.
/// Supports multiple entry points via query params.
/// </summary>
public class SendWizardViewModel
{
    // Store context
    public string StoreId { get; set; } = "";

    // Query param inputs (for pre-loading)
    public string? VtxoOutpoints { get; set; }
    public string? Destinations { get; set; }
    public string? Destination { get; set; }

    // Hydrated data
    public List<ArkVtxo> AvailableVtxos { get; set; } = new();
    public List<ArkVtxo> SelectedVtxos { get; set; } = new();
    public List<SendOutputViewModel> Outputs { get; set; } = new();

    // Computed state
    public SpendType? DetectedSpendType { get; set; }
    public string CoinSelectionMode { get; set; } = "auto";

    // Balance summary
    public ArkBalancesViewModel? Balances { get; set; }

    // Fee estimation
    public long? EstimatedFeeSats { get; set; }
    public string? FeeDescription { get; set; }

    // Validation
    public List<string> Errors { get; set; } = new();

    // Computed properties
    public long TotalSelectedSats => SelectedVtxos.Sum(v => (long)v.Amount);
    public decimal TotalSelectedBtc => TotalSelectedSats / 100_000_000m;
    public int SelectedCount => SelectedVtxos.Count;
    public bool HasPreselectedCoins => !string.IsNullOrEmpty(VtxoOutpoints);
    public bool HasPrefilledDestination => !string.IsNullOrEmpty(Destinations) || !string.IsNullOrEmpty(Destination);
}

public class SendOutputViewModel
{
    public string Destination { get; set; } = "";
    public decimal? AmountBtc { get; set; }
    public long? AmountSats => AmountBtc.HasValue ? (long)(AmountBtc.Value * 100_000_000) : null;
    public DestinationType? DetectedType { get; set; }
    public string? Error { get; set; }
}

public enum SpendType
{
    Offchain,  // Direct VTXO transfer (Ark to Ark, non-recoverable)
    Batch,     // Join Ark batch (onchain output or recoverable coins)
    Swap       // Lightning swap via Boltz
}

public enum DestinationType
{
    ArkAddress,
    BitcoinAddress,
    LightningInvoice,
    Bip21Uri
}
```

**Step 2: Verify compilation**

Run: `dotnet build BTCPayServer.Plugins.ArkPayServer`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Models/SendWizardViewModel.cs
git commit -m "feat(send-wizard): add SendWizardViewModel"
```

---

## Task 2: Create API DTOs for Coin Suggestion

**Files:**
- Create: `Models/Api/SuggestCoinsRequest.cs`
- Create: `Models/Api/SuggestCoinsResponse.cs`

**Step 1: Write the request DTO**

```csharp
namespace BTCPayServer.Plugins.ArkPayServer.Models.Api;

/// <summary>
/// Request to suggest optimal coin selection for a destination.
/// </summary>
public class SuggestCoinsRequest
{
    /// <summary>
    /// Destination type detected from address/invoice.
    /// </summary>
    public DestinationType DestinationType { get; set; }

    /// <summary>
    /// Required amount in satoshis. Null means "send all".
    /// </summary>
    public long? AmountSats { get; set; }

    /// <summary>
    /// Outpoints to exclude from selection (already used elsewhere).
    /// </summary>
    public List<string>? ExcludeOutpoints { get; set; }
}
```

**Step 2: Write the response DTO**

```csharp
namespace BTCPayServer.Plugins.ArkPayServer.Models.Api;

/// <summary>
/// Response with suggested coin selection.
/// </summary>
public class SuggestCoinsResponse
{
    /// <summary>
    /// Suggested outpoints to use (txid:vout format).
    /// </summary>
    public List<string> SuggestedOutpoints { get; set; } = new();

    /// <summary>
    /// Total amount of suggested coins in satoshis.
    /// </summary>
    public long TotalSats { get; set; }

    /// <summary>
    /// Detected spend type based on destination and coin availability.
    /// </summary>
    public SpendType SpendType { get; set; }

    /// <summary>
    /// Warning message if selection is suboptimal.
    /// </summary>
    public string? Warning { get; set; }

    /// <summary>
    /// Error if no valid selection possible.
    /// </summary>
    public string? Error { get; set; }
}
```

**Step 3: Verify compilation**

Run: `dotnet build BTCPayServer.Plugins.ArkPayServer`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Models/Api/SuggestCoinsRequest.cs
git add BTCPayServer.Plugins.ArkPayServer/Models/Api/SuggestCoinsResponse.cs
git commit -m "feat(send-wizard): add coin suggestion API DTOs"
```

---

## Task 3: Implement SuggestCoins API Endpoint

**Files:**
- Modify: `Controllers/ArkController.cs`

**Step 1: Add the endpoint method**

Add after the existing `EstimateFees` endpoint (around line 734):

```csharp
/// <summary>
/// Suggests optimal coin selection based on destination type and amount.
/// </summary>
[HttpPost("stores/{storeId}/suggest-coins")]
public async Task<IActionResult> SuggestCoins(
    string storeId,
    [FromBody] SuggestCoinsRequest request,
    CancellationToken token)
{
    var (store, config, errorResult) = ValidateStoreAndConfig(requireOwnedByStore: false);
    if (errorResult != null)
        return Json(new SuggestCoinsResponse { Error = "Store not configured" });

    try
    {
        var allCoins = await arkadeSpender.GetAvailableCoins(config.WalletId!, token);

        // Filter out excluded outpoints
        var excludeSet = request.ExcludeOutpoints?
            .Select(o => o.Trim())
            .ToHashSet() ?? new HashSet<string>();

        var availableCoins = allCoins
            .Where(c => !excludeSet.Contains($"{c.Vtxo.TransactionId}:{c.Vtxo.TransactionOutputIndex}"))
            .ToList();

        if (!availableCoins.Any())
        {
            return Json(new SuggestCoinsResponse { Error = "No spendable coins available" });
        }

        // Separate by recoverability
        var nonRecoverable = availableCoins.Where(c => !c.Vtxo.Swept).ToList();
        var recoverable = availableCoins.Where(c => c.Vtxo.Swept).ToList();

        var response = new SuggestCoinsResponse();

        // Lightning requires non-recoverable coins only
        if (request.DestinationType == DestinationType.LightningInvoice)
        {
            if (!nonRecoverable.Any())
            {
                return Json(new SuggestCoinsResponse
                {
                    Error = "Lightning requires non-recoverable coins. No non-recoverable coins available."
                });
            }

            response = SelectCoins(nonRecoverable, request.AmountSats, SpendType.Swap);
        }
        // Ark address: prefer offchain (non-recoverable), fallback to batch (recoverable)
        else if (request.DestinationType == DestinationType.ArkAddress)
        {
            // Try offchain first with non-recoverable
            if (nonRecoverable.Any())
            {
                var offchainAttempt = SelectCoins(nonRecoverable, request.AmountSats, SpendType.Offchain);
                if (offchainAttempt.Error == null)
                {
                    response = offchainAttempt;
                }
                else if (recoverable.Any())
                {
                    // Fallback to batch with all coins
                    response = SelectCoins(availableCoins, request.AmountSats, SpendType.Batch);
                    response.Warning = "Using batch mode (recoverable coins included)";
                }
                else
                {
                    response = offchainAttempt; // Return the error
                }
            }
            else if (recoverable.Any())
            {
                // Only recoverable available - must use batch
                response = SelectCoins(recoverable, request.AmountSats, SpendType.Batch);
                response.Warning = "Offchain not available - only recoverable coins";
            }
            else
            {
                response.Error = "No spendable coins available";
            }
        }
        // Bitcoin address: always batch
        else
        {
            response = SelectCoins(availableCoins, request.AmountSats, SpendType.Batch);
        }

        return Json(response);
    }
    catch (Exception ex)
    {
        return Json(new SuggestCoinsResponse { Error = ex.Message });
    }
}

private static SuggestCoinsResponse SelectCoins(
    List<ArkCoin> coins,
    long? targetSats,
    SpendType spendType)
{
    if (!coins.Any())
    {
        return new SuggestCoinsResponse { Error = "No coins available" };
    }

    // Sort by amount descending for efficient selection
    var sorted = coins.OrderByDescending(c => c.Vtxo.Amount).ToList();

    // If no target, select all (send-all mode)
    if (!targetSats.HasValue)
    {
        return new SuggestCoinsResponse
        {
            SuggestedOutpoints = sorted.Select(c => $"{c.Vtxo.TransactionId}:{c.Vtxo.TransactionOutputIndex}").ToList(),
            TotalSats = sorted.Sum(c => (long)c.Vtxo.Amount),
            SpendType = spendType
        };
    }

    // Greedy selection to meet target
    var selected = new List<ArkCoin>();
    long total = 0;

    foreach (var coin in sorted)
    {
        selected.Add(coin);
        total += (long)coin.Vtxo.Amount;
        if (total >= targetSats.Value)
            break;
    }

    if (total < targetSats.Value)
    {
        return new SuggestCoinsResponse
        {
            Error = $"Insufficient funds. Need {targetSats.Value} sats but only {total} sats available."
        };
    }

    return new SuggestCoinsResponse
    {
        SuggestedOutpoints = selected.Select(c => $"{c.Vtxo.TransactionId}:{c.Vtxo.TransactionOutputIndex}").ToList(),
        TotalSats = total,
        SpendType = spendType
    };
}
```

**Step 2: Add required using**

At the top of the file, ensure these are present:
```csharp
using BTCPayServer.Plugins.ArkPayServer.Models.Api;
```

**Step 3: Verify compilation**

Run: `dotnet build BTCPayServer.Plugins.ArkPayServer`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Controllers/ArkController.cs
git commit -m "feat(send-wizard): add suggest-coins API endpoint"
```

---

## Task 4: Implement ValidateSpend API Endpoint

**Files:**
- Modify: `Controllers/ArkController.cs`
- Create: `Models/Api/ValidateSpendRequest.cs`
- Create: `Models/Api/ValidateSpendResponse.cs`

**Step 1: Create request DTO**

```csharp
namespace BTCPayServer.Plugins.ArkPayServer.Models.Api;

/// <summary>
/// Pre-flight validation request before executing spend.
/// </summary>
public class ValidateSpendRequest
{
    public List<string> VtxoOutpoints { get; set; } = new();
    public List<ValidateSpendOutput> Outputs { get; set; } = new();
}

public class ValidateSpendOutput
{
    public string Destination { get; set; } = "";
    public long? AmountSats { get; set; }
}
```

**Step 2: Create response DTO**

```csharp
namespace BTCPayServer.Plugins.ArkPayServer.Models.Api;

/// <summary>
/// Validation result with any errors found.
/// </summary>
public class ValidateSpendResponse
{
    public bool IsValid { get; set; }
    public SpendType? SpendType { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<OutputValidationResult> OutputResults { get; set; } = new();
}

public class OutputValidationResult
{
    public int Index { get; set; }
    public DestinationType? DetectedType { get; set; }
    public string? Error { get; set; }
}
```

**Step 3: Add the endpoint**

Add to `ArkController.cs`:

```csharp
/// <summary>
/// Pre-flight validation before executing spend.
/// </summary>
[HttpPost("stores/{storeId}/validate-spend")]
public async Task<IActionResult> ValidateSpend(
    string storeId,
    [FromBody] ValidateSpendRequest request,
    CancellationToken token)
{
    var (store, config, errorResult) = ValidateStoreAndConfig(requireOwnedByStore: false);
    if (errorResult != null)
        return Json(new ValidateSpendResponse { Errors = { "Store not configured" } });

    var response = new ValidateSpendResponse();
    var hasLightning = false;
    var hasRecoverableCoins = false;

    // Validate coins exist and are spendable
    if (request.VtxoOutpoints.Any())
    {
        var outpoints = ParseOutpoints(request.VtxoOutpoints.ToArray());
        var filter = new VtxoFilter
        {
            WalletIds = new[] { config.WalletId! },
            Outpoints = outpoints.ToList(),
            IncludeSpent = false,
            IncludeRecoverable = true
        };

        var vtxos = await vtxoStorage.GetVtxos(filter, token);

        if (vtxos.Count != request.VtxoOutpoints.Count)
        {
            response.Errors.Add("Some selected coins are no longer available");
        }

        hasRecoverableCoins = vtxos.Any(v => v.Swept);
    }
    else
    {
        response.Errors.Add("No coins selected");
    }

    // Validate each output
    for (int i = 0; i < request.Outputs.Count; i++)
    {
        var output = request.Outputs[i];
        var result = new OutputValidationResult { Index = i };

        if (string.IsNullOrWhiteSpace(output.Destination))
        {
            result.Error = "Destination required";
        }
        else
        {
            var (dest, amount, outputType) = ParseOutputDestination(output.Destination);

            if (dest == null)
            {
                result.Error = "Invalid address format";
            }
            else if (output.Destination.StartsWith("ln", StringComparison.OrdinalIgnoreCase) ||
                     output.Destination.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase))
            {
                result.DetectedType = DestinationType.LightningInvoice;
                hasLightning = true;
            }
            else if (outputType == ArkTxOutType.Vtxo)
            {
                result.DetectedType = DestinationType.ArkAddress;
            }
            else
            {
                result.DetectedType = DestinationType.BitcoinAddress;
            }
        }

        response.OutputResults.Add(result);
    }

    // Cross-validation rules
    if (hasLightning)
    {
        if (request.Outputs.Count > 1)
        {
            response.Errors.Add("Lightning supports single output only");
        }
        if (hasRecoverableCoins)
        {
            response.Errors.Add("Lightning requires non-recoverable coins");
        }
        response.SpendType = SpendType.Swap;
    }
    else if (response.OutputResults.Any(r => r.DetectedType == DestinationType.BitcoinAddress))
    {
        response.SpendType = SpendType.Batch;
    }
    else if (hasRecoverableCoins)
    {
        response.SpendType = SpendType.Batch;
    }
    else
    {
        response.SpendType = SpendType.Offchain;
    }

    response.IsValid = !response.Errors.Any() && !response.OutputResults.Any(r => r.Error != null);
    return Json(response);
}
```

**Step 4: Verify compilation**

Run: `dotnet build BTCPayServer.Plugins.ArkPayServer`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Models/Api/ValidateSpendRequest.cs
git add BTCPayServer.Plugins.ArkPayServer/Models/Api/ValidateSpendResponse.cs
git add BTCPayServer.Plugins.ArkPayServer/Controllers/ArkController.cs
git commit -m "feat(send-wizard): add validate-spend API endpoint"
```

---

## Task 5: Add Send Controller Action (GET)

**Files:**
- Modify: `Controllers/ArkController.cs`

**Step 1: Add the GET action**

```csharp
/// <summary>
/// Unified Send Wizard - main entry point.
/// </summary>
[HttpGet("stores/{storeId}/send")]
public async Task<IActionResult> Send(
    string storeId,
    string? vtxos,
    string? destinations,
    string? destination,
    CancellationToken token)
{
    var (store, config, errorResult) = ValidateStoreAndConfig(requireOwnedByStore: false);
    if (errorResult != null)
        return errorResult;

    var model = new SendWizardViewModel
    {
        StoreId = storeId,
        VtxoOutpoints = vtxos,
        Destinations = destinations,
        Destination = destination
    };

    // Load balances
    model.Balances = await GetArkBalances(config.WalletId!, token);

    // Load available (spendable) coins
    var allCoins = await arkadeSpender.GetAvailableCoins(config.WalletId!, token);
    model.AvailableVtxos = allCoins.Select(c => c.Vtxo).ToList();

    if (!model.AvailableVtxos.Any())
    {
        model.Errors.Add("No spendable coins available");
        return View("Send", model);
    }

    // Handle pre-selected VTXOs from query param
    if (!string.IsNullOrEmpty(vtxos))
    {
        var requestedOutpoints = vtxos.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToHashSet();

        model.SelectedVtxos = model.AvailableVtxos
            .Where(v => requestedOutpoints.Contains($"{v.TransactionId}:{v.TransactionOutputIndex}"))
            .ToList();

        model.CoinSelectionMode = "manual";

        // Warn if some requested coins unavailable
        if (model.SelectedVtxos.Count < requestedOutpoints.Count)
        {
            var found = model.SelectedVtxos
                .Select(v => $"{v.TransactionId}:{v.TransactionOutputIndex}")
                .ToHashSet();
            var missing = requestedOutpoints.Except(found).Count();
            model.Errors.Add($"{missing} selected coin(s) no longer available");
        }
    }

    // Handle pre-filled destinations
    if (!string.IsNullOrEmpty(destinations))
    {
        // Format: addr1:amt1,addr2:amt2,...
        var parts = destinations.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var segments = part.Split(':', 2);
            var output = new SendOutputViewModel
            {
                Destination = segments[0].Trim()
            };

            if (segments.Length > 1 && decimal.TryParse(segments[1], out var amt))
            {
                output.AmountBtc = amt;
            }

            model.Outputs.Add(output);
        }
    }
    else if (!string.IsNullOrEmpty(destination))
    {
        // Single destination (BIP21, address, invoice)
        model.Outputs.Add(new SendOutputViewModel { Destination = destination });
    }
    else
    {
        // Default: one empty output row
        model.Outputs.Add(new SendOutputViewModel());
    }

    return View("Send", model);
}
```

**Step 2: Verify compilation**

Run: `dotnet build BTCPayServer.Plugins.ArkPayServer`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Controllers/ArkController.cs
git commit -m "feat(send-wizard): add Send GET action with query param handling"
```

---

## Task 6: Add Send Controller Action (POST)

**Files:**
- Modify: `Controllers/ArkController.cs`

**Step 1: Add the POST action**

```csharp
/// <summary>
/// Execute the send transaction.
/// </summary>
[HttpPost("stores/{storeId}/send")]
public async Task<IActionResult> Send(
    string storeId,
    [FromForm] SendWizardViewModel model,
    [FromForm] string[] selectedVtxoOutpoints,
    CancellationToken token)
{
    var (store, config, errorResult) = ValidateStoreAndConfig(requireOwnedByStore: false);
    if (errorResult != null)
        return errorResult;

    model.StoreId = storeId;
    model.Balances = await GetArkBalances(config.WalletId!, token);

    // Re-load available coins
    var allCoins = await arkadeSpender.GetAvailableCoins(config.WalletId!, token);
    model.AvailableVtxos = allCoins.Select(c => c.Vtxo).ToList();

    // Validate selected coins
    if (!selectedVtxoOutpoints.Any())
    {
        model.Errors.Add("No coins selected");
        return View("Send", model);
    }

    var selectedSet = selectedVtxoOutpoints.ToHashSet();
    var selectedCoins = allCoins
        .Where(c => selectedSet.Contains($"{c.Vtxo.TransactionId}:{c.Vtxo.TransactionOutputIndex}"))
        .ToList();

    if (selectedCoins.Count != selectedVtxoOutpoints.Length)
    {
        model.Errors.Add("Some selected coins are no longer available");
        return View("Send", model);
    }

    model.SelectedVtxos = selectedCoins.Select(c => c.Vtxo).ToList();

    // Validate outputs
    var validOutputs = model.Outputs.Where(o => !string.IsNullOrWhiteSpace(o.Destination)).ToList();
    if (!validOutputs.Any())
    {
        model.Errors.Add("At least one destination required");
        return View("Send", model);
    }

    // Check for Lightning
    var isLightning = validOutputs.Any(o =>
        o.Destination.StartsWith("ln", StringComparison.OrdinalIgnoreCase) ||
        o.Destination.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase));

    if (isLightning)
    {
        if (validOutputs.Count > 1)
        {
            model.Errors.Add("Lightning supports single output only");
            return View("Send", model);
        }

        if (selectedCoins.Any(c => c.Vtxo.Swept))
        {
            model.Errors.Add("Lightning requires non-recoverable coins");
            return View("Send", model);
        }

        // Execute Lightning payment
        try
        {
            var lnDestination = validOutputs[0].Destination;
            await arkadeSpendingService.Spend(store!, lnDestination, token);
            return RedirectWithSuccess(nameof(StoreOverview), "Lightning payment sent!", new { storeId });
        }
        catch (Exception ex)
        {
            model.Errors.Add($"Lightning payment failed: {ex.Message}");
            return View("Send", model);
        }
    }

    // Parse all destinations
    var outputs = new List<(IDestination dest, Money? amount, ArkTxOutType type)>();
    for (int i = 0; i < validOutputs.Count; i++)
    {
        var output = validOutputs[i];
        var (dest, amount, outputType) = ParseOutputDestination(output.Destination);

        if (dest == null)
        {
            output.Error = "Invalid address format";
            model.Errors.Add($"Output {i + 1}: Invalid address format");
            continue;
        }

        // Use output's amount if specified, else from parsed destination
        var finalAmount = output.AmountSats.HasValue
            ? Money.Satoshis(output.AmountSats.Value)
            : amount;

        outputs.Add((dest, finalAmount, outputType));
    }

    if (model.Errors.Any())
    {
        return View("Send", model);
    }

    // Execute the spend
    try
    {
        var result = await arkadeSpender.Spend(
            config.WalletId!,
            selectedCoins.ToArray(),
            outputs.Select(o => (o.dest, o.amount, o.type)).ToArray(),
            token);

        // Poll for VTXO updates
        var scripts = await efCoreContractStorage.GetActiveContractScriptsAsync(token);
        await vtxoPollingService.PollScriptsForVtxos(scripts, token);

        return RedirectWithSuccess(nameof(StoreOverview), "Transaction sent successfully!", new { storeId });
    }
    catch (Exception ex)
    {
        model.Errors.Add($"Transaction failed: {ex.Message}");
        return View("Send", model);
    }
}
```

**Step 2: Verify compilation**

Run: `dotnet build BTCPayServer.Plugins.ArkPayServer`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Controllers/ArkController.cs
git commit -m "feat(send-wizard): add Send POST action for transaction execution"
```

---

## Task 7: Create Send View - Basic Structure

**Files:**
- Create: `Views/Ark/Send.cshtml`

**Step 1: Create the view file with basic structure**

```razor
@using BTCPayServer.Abstractions.Contracts
@using BTCPayServer.Abstractions.Extensions
@using NBitcoin
@model BTCPayServer.Plugins.ArkPayServer.Models.SendWizardViewModel
@inject IScopeProvider ScopeProvider

@{
    ViewData.SetActivePage(category: "Ark", activePage: "Send", title: "Ark - Send");
    var storeId = ScopeProvider.GetCurrentStoreId();
}

@section PageHeadContent
{
    <link rel="stylesheet" href="~/plugins/ArkPayServer/css/ark-plugin.css" asp-append-version="true" />
    <style>
        .wizard-section {
            border: 1px solid var(--btcpay-body-border-light);
            border-radius: 4px;
            margin-bottom: 1rem;
        }
        .wizard-section-header {
            padding: 0.75rem 1rem;
            background: var(--btcpay-body-bg);
            cursor: pointer;
            display: flex;
            justify-content: space-between;
            align-items: center;
        }
        .wizard-section-header:hover {
            background: var(--btcpay-body-bg-hover);
        }
        .wizard-section-body {
            padding: 1rem;
            border-top: 1px solid var(--btcpay-body-border-light);
        }
        .wizard-section.collapsed .wizard-section-body {
            display: none;
        }
        .wizard-section.collapsed .wizard-section-header .section-icon {
            transform: rotate(-90deg);
        }
        .section-icon {
            transition: transform 0.2s;
        }
        .section-summary {
            font-size: 0.875rem;
            color: var(--btcpay-body-text-muted);
        }
        .output-row {
            border: 1px solid var(--btcpay-body-border-light);
            border-radius: 4px;
            padding: 1rem;
            margin-bottom: 0.5rem;
        }
        .coin-summary {
            display: flex;
            gap: 1rem;
            align-items: center;
        }
        .spend-type-badge {
            font-size: 0.75rem;
            padding: 0.25rem 0.5rem;
        }
    </style>
}

<div class="sticky-header">
    <h2>@ViewData["Title"]</h2>
</div>

<partial name="_StatusMessage" />

@if (Model.Errors.Any())
{
    <div class="alert alert-danger">
        <ul class="mb-0">
            @foreach (var error in Model.Errors)
            {
                <li>@error</li>
            }
        </ul>
    </div>
}

@if (!Model.AvailableVtxos.Any())
{
    <div class="alert alert-warning">
        <p class="mb-0" text-translate="true">No spendable coins available. Receive some funds first.</p>
    </div>

    <partial name="_ArkBalances" model="Model.Balances" />
}
else
{
    <form method="post" asp-action="Send" asp-route-storeId="@Model.StoreId" id="send-wizard-form">
        <!-- Destination Section -->
        <div class="wizard-section" id="destination-section">
            <div class="wizard-section-header" onclick="toggleSection('destination-section')">
                <div>
                    <vc:icon symbol="caret-down" class="section-icon" />
                    <strong text-translate="true">Destination</strong>
                </div>
                <span class="section-summary" id="destination-summary"></span>
            </div>
            <div class="wizard-section-body">
                <div id="outputs-container">
                    @for (int i = 0; i < Model.Outputs.Count; i++)
                    {
                        <div class="output-row" data-index="@i">
                            <div class="row g-3">
                                <div class="col-12 col-md-8">
                                    <label class="form-label" text-translate="true">Address, Invoice, or BIP21</label>
                                    <input type="text"
                                           name="Outputs[@i].Destination"
                                           value="@Model.Outputs[i].Destination"
                                           class="form-control destination-input"
                                           placeholder="Paste address, invoice, or BIP21 URI..."
                                           data-index="@i" />
                                    @if (!string.IsNullOrEmpty(Model.Outputs[i].Error))
                                    {
                                        <div class="text-danger small mt-1">@Model.Outputs[i].Error</div>
                                    }
                                </div>
                                <div class="col-12 col-md-3">
                                    <label class="form-label" text-translate="true">Amount (BTC)</label>
                                    <input type="number"
                                           name="Outputs[@i].AmountBtc"
                                           value="@Model.Outputs[i].AmountBtc"
                                           class="form-control amount-input"
                                           placeholder="Optional"
                                           step="0.00000001"
                                           min="0"
                                           data-index="@i" />
                                </div>
                                <div class="col-12 col-md-1 d-flex align-items-end">
                                    @if (i > 0)
                                    {
                                        <button type="button" class="btn btn-outline-danger remove-output-btn" data-index="@i">
                                            <vc:icon symbol="cross" />
                                        </button>
                                    }
                                </div>
                            </div>
                            <div class="mt-2">
                                <span class="destination-type-badge badge" data-index="@i"></span>
                            </div>
                        </div>
                    }
                </div>
                <button type="button" id="add-output-btn" class="btn btn-link p-0 mt-2">
                    <vc:icon symbol="actions-add" />
                    <span text-translate="true">Add another output</span>
                </button>
            </div>
        </div>

        <!-- Coins Section -->
        <div class="wizard-section @(Model.HasPreselectedCoins ? "" : "collapsed")" id="coins-section">
            <div class="wizard-section-header" onclick="toggleSection('coins-section')">
                <div>
                    <vc:icon symbol="caret-down" class="section-icon" />
                    <strong text-translate="true">Coins</strong>
                </div>
                <div class="coin-summary">
                    <span id="coins-summary">@Model.SelectedCount selected · @Model.TotalSelectedBtc.ToString("0.00000000") BTC</span>
                    <span class="badge text-bg-secondary" id="selection-mode-badge">@Model.CoinSelectionMode</span>
                </div>
            </div>
            <div class="wizard-section-body">
                <div class="table-responsive">
                    <table class="table table-sm" id="coins-table">
                        <thead>
                            <tr>
                                <th style="width: 32px;">
                                    <input type="checkbox" class="form-check-input" id="select-all-coins" />
                                </th>
                                <th text-translate="true">Outpoint</th>
                                <th text-translate="true">Amount</th>
                                <th text-translate="true">Status</th>
                            </tr>
                        </thead>
                        <tbody>
                            @foreach (var vtxo in Model.AvailableVtxos)
                            {
                                var outpoint = $"{vtxo.TransactionId}:{vtxo.TransactionOutputIndex}";
                                var isSelected = Model.SelectedVtxos.Any(s =>
                                    s.TransactionId == vtxo.TransactionId &&
                                    s.TransactionOutputIndex == vtxo.TransactionOutputIndex);

                                <tr>
                                    <td>
                                        <input type="checkbox"
                                               name="selectedVtxoOutpoints"
                                               value="@outpoint"
                                               class="form-check-input coin-checkbox"
                                               @(isSelected ? "checked" : "") />
                                    </td>
                                    <td>
                                        <code class="small">@outpoint.Substring(0, 8)...@outpoint.Substring(outpoint.Length - 6)</code>
                                    </td>
                                    <td>@Money.Satoshis((long)vtxo.Amount).ToDecimal(MoneyUnit.BTC) BTC</td>
                                    <td>
                                        @if (vtxo.Swept)
                                        {
                                            <span class="badge text-bg-warning">Recoverable</span>
                                        }
                                        else
                                        {
                                            <span class="badge text-bg-success">Spendable</span>
                                        }
                                    </td>
                                </tr>
                            }
                        </tbody>
                    </table>
                </div>
            </div>
        </div>

        <!-- Review Section -->
        <div class="wizard-section collapsed" id="review-section">
            <div class="wizard-section-header" onclick="toggleSection('review-section')">
                <div>
                    <vc:icon symbol="caret-down" class="section-icon" />
                    <strong text-translate="true">Review</strong>
                </div>
                <span class="section-summary" id="review-summary"></span>
            </div>
            <div class="wizard-section-body">
                <div id="review-content">
                    <p text-translate="true">Complete the destination to see transaction summary.</p>
                </div>
            </div>
        </div>

        <!-- Footer -->
        <div class="d-flex justify-content-between align-items-center mt-4">
            <div id="fee-display">
                <span class="text-muted" text-translate="true">Fee:</span>
                <span id="fee-amount">--</span>
            </div>
            <div class="d-flex gap-2">
                <a asp-action="StoreOverview" asp-route-storeId="@Model.StoreId" class="btn btn-secondary" text-translate="true">Cancel</a>
                <button type="submit" class="btn btn-primary" id="send-btn" disabled>
                    <span text-translate="true">Send</span>
                </button>
            </div>
        </div>
    </form>
}
```

**Step 2: Verify the view renders**

Run: `dotnet build BTCPayServer.Plugins.ArkPayServer`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Views/Ark/Send.cshtml
git commit -m "feat(send-wizard): create Send.cshtml view structure"
```

---

## Task 8: Add Send View JavaScript - State Management

**Files:**
- Modify: `Views/Ark/Send.cshtml`

**Step 1: Add the JavaScript section at the bottom of the view**

Add before the closing `}` of the main else block:

```razor
@section PageFootContent
{
    <script>
    (function() {
        'use strict';

        // State
        const state = {
            coinSelectionMode: '@Model.CoinSelectionMode',
            detectedSpendType: null,
            estimatedFee: null,
            isLightning: false
        };

        // DOM elements
        const form = document.getElementById('send-wizard-form');
        const outputsContainer = document.getElementById('outputs-container');
        const addOutputBtn = document.getElementById('add-output-btn');
        const coinsTable = document.getElementById('coins-table');
        const selectAllCoins = document.getElementById('select-all-coins');
        const sendBtn = document.getElementById('send-btn');
        const feeDisplay = document.getElementById('fee-amount');
        const coinsSummary = document.getElementById('coins-summary');
        const selectionModeBadge = document.getElementById('selection-mode-badge');
        const reviewSummary = document.getElementById('review-summary');
        const reviewContent = document.getElementById('review-content');

        // Debounce helper
        function debounce(fn, delay) {
            let timeout;
            return function(...args) {
                clearTimeout(timeout);
                timeout = setTimeout(() => fn.apply(this, args), delay);
            };
        }

        // Toggle section collapse
        window.toggleSection = function(sectionId) {
            const section = document.getElementById(sectionId);
            section.classList.toggle('collapsed');
        };

        // Get selected coins
        function getSelectedCoins() {
            const checkboxes = coinsTable.querySelectorAll('.coin-checkbox:checked');
            return Array.from(checkboxes).map(cb => cb.value);
        }

        // Update coins summary
        function updateCoinsSummary() {
            const checkboxes = coinsTable.querySelectorAll('.coin-checkbox:checked');
            const count = checkboxes.length;
            let totalSats = 0;

            checkboxes.forEach(cb => {
                const row = cb.closest('tr');
                const amountText = row.cells[2].textContent;
                const btc = parseFloat(amountText);
                totalSats += Math.round(btc * 100000000);
            });

            const totalBtc = (totalSats / 100000000).toFixed(8);
            coinsSummary.textContent = `${count} selected · ${totalBtc} BTC`;
            selectionModeBadge.textContent = state.coinSelectionMode;
        }

        // Get outputs from form
        function getOutputs() {
            const outputs = [];
            const rows = outputsContainer.querySelectorAll('.output-row');

            rows.forEach((row, i) => {
                const destInput = row.querySelector('.destination-input');
                const amountInput = row.querySelector('.amount-input');

                outputs.push({
                    destination: destInput?.value?.trim() || '',
                    amountBtc: amountInput?.value ? parseFloat(amountInput.value) : null
                });
            });

            return outputs;
        }

        // Detect destination type
        function detectDestinationType(destination) {
            if (!destination) return null;

            const lower = destination.toLowerCase();
            if (lower.startsWith('ln') || lower.startsWith('lightning:')) {
                return 'LightningInvoice';
            }
            if (lower.startsWith('bitcoin:') || lower.startsWith('ark:')) {
                return 'Bip21Uri';
            }
            // Simple heuristic: Ark addresses are typically longer
            if (destination.length > 60) {
                return 'ArkAddress';
            }
            return 'BitcoinAddress';
        }

        // Update destination badges
        function updateDestinationBadges() {
            const rows = outputsContainer.querySelectorAll('.output-row');
            let hasLightning = false;

            rows.forEach((row, i) => {
                const destInput = row.querySelector('.destination-input');
                const badge = row.querySelector('.destination-type-badge');
                const type = detectDestinationType(destInput?.value);

                if (type === 'LightningInvoice') hasLightning = true;

                if (badge) {
                    badge.textContent = type || '';
                    badge.className = 'destination-type-badge badge ' + (type ? 'text-bg-info' : '');
                }
            });

            state.isLightning = hasLightning;

            // Disable add output for Lightning
            if (addOutputBtn) {
                addOutputBtn.disabled = hasLightning;
                addOutputBtn.title = hasLightning ? 'Lightning supports single output only' : '';
            }
        }

        // Estimate fees
        const estimateFees = debounce(async function() {
            const selectedCoins = getSelectedCoins();
            const outputs = getOutputs().filter(o => o.destination);

            if (!selectedCoins.length || !outputs.length) {
                feeDisplay.textContent = '--';
                sendBtn.disabled = true;
                return;
            }

            // Calculate total input
            let totalInputSats = 0;
            const checkboxes = coinsTable.querySelectorAll('.coin-checkbox:checked');
            checkboxes.forEach(cb => {
                const row = cb.closest('tr');
                const amountText = row.cells[2].textContent;
                const btc = parseFloat(amountText);
                totalInputSats += Math.round(btc * 100000000);
            });

            try {
                const response = await fetch(`/plugins/ark/stores/@Model.StoreId/estimate-fees`, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'RequestVerificationToken': document.querySelector('input[name="__RequestVerificationToken"]')?.value
                    },
                    body: JSON.stringify({
                        vtxoOutpoints: selectedCoins,
                        totalInputSats: totalInputSats,
                        outputs: outputs.map(o => ({
                            destination: o.destination,
                            amountSats: o.amountBtc ? Math.round(o.amountBtc * 100000000) : null
                        }))
                    })
                });

                const data = await response.json();

                if (data.error) {
                    feeDisplay.textContent = data.error;
                    sendBtn.disabled = true;
                } else {
                    const feeBtc = (data.estimatedFeeSats / 100000000).toFixed(8);
                    feeDisplay.textContent = `~${data.estimatedFeeSats} sats (${feeBtc} BTC)`;
                    if (data.feeDescription) {
                        feeDisplay.textContent += ` - ${data.feeDescription}`;
                    }
                    state.estimatedFee = data.estimatedFeeSats;
                    sendBtn.disabled = false;

                    // Update review section
                    updateReviewSection(selectedCoins, outputs, data);
                }
            } catch (err) {
                console.error('Fee estimation failed:', err);
                feeDisplay.textContent = 'Error estimating fee';
                sendBtn.disabled = true;
            }
        }, 500);

        // Update review section
        function updateReviewSection(coins, outputs, feeData) {
            const totalInput = coins.length;
            const totalOutputs = outputs.length;

            let spendType = 'Batch';
            if (state.isLightning) spendType = 'Lightning Swap';
            else if (feeData.isOffchain) spendType = 'Offchain';

            reviewSummary.textContent = `${totalOutputs} output(s) via ${spendType}`;

            let html = '<dl class="row mb-0">';
            html += `<dt class="col-sm-4">Inputs</dt><dd class="col-sm-8">${totalInput} coin(s)</dd>`;
            html += `<dt class="col-sm-4">Outputs</dt><dd class="col-sm-8">${totalOutputs}</dd>`;
            html += `<dt class="col-sm-4">Spend Type</dt><dd class="col-sm-8"><span class="badge text-bg-primary">${spendType}</span></dd>`;
            html += `<dt class="col-sm-4">Fee</dt><dd class="col-sm-8">${feeData.estimatedFeeSats} sats</dd>`;
            html += '</dl>';

            reviewContent.innerHTML = html;
        }

        // Suggest coins based on destination
        async function suggestCoins() {
            if (state.coinSelectionMode !== 'auto') return;

            const outputs = getOutputs().filter(o => o.destination);
            if (!outputs.length) return;

            const firstDest = outputs[0].destination;
            const destType = detectDestinationType(firstDest);
            if (!destType) return;

            // Calculate total amount needed
            let totalSats = null;
            const amounts = outputs.map(o => o.amountBtc ? Math.round(o.amountBtc * 100000000) : null);
            if (amounts.every(a => a !== null)) {
                totalSats = amounts.reduce((sum, a) => sum + a, 0);
            }

            try {
                const response = await fetch(`/plugins/ark/stores/@Model.StoreId/suggest-coins`, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'RequestVerificationToken': document.querySelector('input[name="__RequestVerificationToken"]')?.value
                    },
                    body: JSON.stringify({
                        destinationType: destType,
                        amountSats: totalSats
                    })
                });

                const data = await response.json();

                if (!data.error && data.suggestedOutpoints?.length) {
                    // Select suggested coins
                    const suggested = new Set(data.suggestedOutpoints);
                    coinsTable.querySelectorAll('.coin-checkbox').forEach(cb => {
                        cb.checked = suggested.has(cb.value);
                    });

                    state.detectedSpendType = data.spendType;
                    updateCoinsSummary();
                    estimateFees();
                }
            } catch (err) {
                console.error('Coin suggestion failed:', err);
            }
        }

        // Add output row
        function addOutputRow() {
            const index = outputsContainer.querySelectorAll('.output-row').length;
            const template = `
                <div class="output-row" data-index="${index}">
                    <div class="row g-3">
                        <div class="col-12 col-md-8">
                            <label class="form-label">Address, Invoice, or BIP21</label>
                            <input type="text"
                                   name="Outputs[${index}].Destination"
                                   class="form-control destination-input"
                                   placeholder="Paste address, invoice, or BIP21 URI..."
                                   data-index="${index}" />
                        </div>
                        <div class="col-12 col-md-3">
                            <label class="form-label">Amount (BTC)</label>
                            <input type="number"
                                   name="Outputs[${index}].AmountBtc"
                                   class="form-control amount-input"
                                   placeholder="Optional"
                                   step="0.00000001"
                                   min="0"
                                   data-index="${index}" />
                        </div>
                        <div class="col-12 col-md-1 d-flex align-items-end">
                            <button type="button" class="btn btn-outline-danger remove-output-btn" data-index="${index}">
                                <span>×</span>
                            </button>
                        </div>
                    </div>
                    <div class="mt-2">
                        <span class="destination-type-badge badge" data-index="${index}"></span>
                    </div>
                </div>
            `;
            outputsContainer.insertAdjacentHTML('beforeend', template);
        }

        // Remove output row and reindex
        function removeOutputRow(index) {
            const row = outputsContainer.querySelector(`.output-row[data-index="${index}"]`);
            if (row) {
                row.remove();
                reindexOutputs();
                updateDestinationBadges();
                estimateFees();
            }
        }

        // Reindex output rows after removal
        function reindexOutputs() {
            const rows = outputsContainer.querySelectorAll('.output-row');
            rows.forEach((row, i) => {
                row.dataset.index = i;
                row.querySelectorAll('[data-index]').forEach(el => el.dataset.index = i);
                row.querySelectorAll('[name*="Outputs["]').forEach(input => {
                    input.name = input.name.replace(/Outputs\[\d+\]/, `Outputs[${i}]`);
                });
            });
        }

        // Event listeners
        addOutputBtn?.addEventListener('click', addOutputRow);

        outputsContainer?.addEventListener('click', function(e) {
            const removeBtn = e.target.closest('.remove-output-btn');
            if (removeBtn) {
                removeOutputRow(parseInt(removeBtn.dataset.index));
            }
        });

        outputsContainer?.addEventListener('input', debounce(function(e) {
            if (e.target.classList.contains('destination-input')) {
                updateDestinationBadges();
                suggestCoins();
            } else if (e.target.classList.contains('amount-input')) {
                estimateFees();
            }
        }, 300));

        coinsTable?.addEventListener('change', function(e) {
            if (e.target.classList.contains('coin-checkbox')) {
                state.coinSelectionMode = 'manual';
                updateCoinsSummary();
                estimateFees();
            }
        });

        selectAllCoins?.addEventListener('change', function() {
            const checkboxes = coinsTable.querySelectorAll('.coin-checkbox');
            checkboxes.forEach(cb => cb.checked = this.checked);
            state.coinSelectionMode = 'manual';
            updateCoinsSummary();
            estimateFees();
        });

        // Initialize
        updateDestinationBadges();
        updateCoinsSummary();

        // If pre-selected coins, estimate fees
        if (getSelectedCoins().length > 0 && getOutputs().some(o => o.destination)) {
            estimateFees();
        }
    })();
    </script>
}
```

**Step 2: Verify the view compiles**

Run: `dotnet build BTCPayServer.Plugins.ArkPayServer`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Views/Ark/Send.cshtml
git commit -m "feat(send-wizard): add JavaScript state management and interactions"
```

---

## Task 9: Update VTXOs Page Mass Action

**Files:**
- Modify: `Controllers/ArkController.cs`

**Step 1: Update MassActionVtxos to redirect to Send wizard**

Find the `MassActionVtxos` method and update the `build-intent` case:

```csharp
case "build-intent":
    // Redirect to new unified Send wizard
    return RedirectToAction(nameof(Send), new { storeId, vtxos = string.Join(",", selectedItems) });
```

**Step 2: Verify compilation**

Run: `dotnet build BTCPayServer.Plugins.ArkPayServer`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Controllers/ArkController.cs
git commit -m "feat(send-wizard): update VTXOs mass action to use Send wizard"
```

---

## Task 10: Add Redirect from Old Pages

**Files:**
- Modify: `Controllers/ArkController.cs`

**Step 1: Add redirect for SpendOverview**

Update or add the `SpendOverview` action to redirect:

```csharp
/// <summary>
/// Legacy redirect - SpendOverview now redirects to Send wizard.
/// </summary>
[HttpGet("stores/{storeId}/spend")]
public IActionResult SpendOverview(string storeId, string? vtxoOutpoints)
{
    // Redirect to new unified Send wizard
    return RedirectToAction(nameof(Send), new { storeId, vtxos = vtxoOutpoints });
}
```

**Step 2: Add redirect for IntentBuilder GET**

Update or add:

```csharp
/// <summary>
/// Legacy redirect - IntentBuilder now redirects to Send wizard.
/// </summary>
[HttpGet("stores/{storeId}/intent-builder")]
public IActionResult IntentBuilder(string storeId, string? vtxoOutpoints)
{
    return RedirectToAction(nameof(Send), new { storeId, vtxos = vtxoOutpoints });
}
```

**Step 3: Verify compilation**

Run: `dotnet build BTCPayServer.Plugins.ArkPayServer`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Controllers/ArkController.cs
git commit -m "feat(send-wizard): add redirects from legacy SpendOverview and IntentBuilder"
```

---

## Task 11: Update Navigation to Use Send

**Files:**
- Modify: `Views/Ark/_ArkMenu.cshtml` (if exists) or relevant navigation partial

**Step 1: Find navigation files**

Search for navigation elements that link to spending/intent pages.

**Step 2: Update links to point to Send**

Replace any links like:
- `/spend` → `/send`
- `/intent-builder` → `/send`
- `asp-action="SpendOverview"` → `asp-action="Send"`
- `asp-action="IntentBuilder"` → `asp-action="Send"`

**Step 3: Verify compilation**

Run: `dotnet build BTCPayServer.Plugins.ArkPayServer`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Views/
git commit -m "feat(send-wizard): update navigation links to use Send"
```

---

## Task 12: Update ArkPayoutHandler

**Files:**
- Modify: `Payouts/ArkPayoutHandler.cs` (or equivalent)

**Step 1: Find the payout handler**

Search for where payouts redirect to spending UI.

**Step 2: Update to use Send wizard with destinations query param**

The payout handler should redirect to:
```
/send?destinations=addr1:amt1,addr2:amt2
```

**Step 3: Verify compilation**

Run: `dotnet build BTCPayServer.Plugins.ArkPayServer`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add BTCPayServer.Plugins.ArkPayServer/Payouts/
git commit -m "feat(send-wizard): update payout handler to use Send wizard"
```

---

## Task 13: Manual Testing Checklist

**Test Cases:**

1. **Direct navigation to /send**
   - [ ] Empty destination field focused
   - [ ] Auto coin selection works when typing destination
   - [ ] Fee estimation updates

2. **From VTXOs page with mass action**
   - [ ] Coins pre-selected
   - [ ] Summary shows correct count and amount
   - [ ] Can modify selection

3. **Ark address destination**
   - [ ] Detected as Ark address
   - [ ] Prefers offchain when possible
   - [ ] Falls back to batch for recoverable

4. **Bitcoin address destination**
   - [ ] Detected as Bitcoin address
   - [ ] Uses batch mode

5. **Lightning invoice**
   - [ ] Detected as Lightning
   - [ ] Add output button disabled
   - [ ] Error if recoverable coins selected

6. **Multi-output**
   - [ ] Can add outputs
   - [ ] Can remove outputs
   - [ ] Reindexing works correctly

7. **Error handling**
   - [ ] Invalid address shows error
   - [ ] Insufficient funds shows error
   - [ ] Transaction failure shows error

**Step 1: Run through test cases manually**

**Step 2: Document any issues found**

**Step 3: Commit any fixes**

---

## Task 14: Cleanup Deprecated Files (Optional)

**Files to consider removing after validation:**
- `Views/Ark/SpendOverview.cshtml`
- `Views/Ark/IntentBuilder.cshtml`
- `Models/SpendOverviewViewModel.cs`
- `Models/IntentBuilderViewModel.cs`

**Note:** Only remove after the Send wizard is fully validated and working in production for a release cycle.

**Step 1: Verify no remaining references**

```bash
grep -r "SpendOverview" --include="*.cs" --include="*.cshtml"
grep -r "IntentBuilder" --include="*.cs" --include="*.cshtml"
```

**Step 2: Remove files if safe**

**Step 3: Commit**

```bash
git add -A
git commit -m "chore: remove deprecated SpendOverview and IntentBuilder"
```

---

## Summary

This plan implements the Unified Send Wizard in 14 tasks:

1. **Tasks 1-2**: Create ViewModels and API DTOs
2. **Tasks 3-4**: Implement API endpoints (suggest-coins, validate-spend)
3. **Tasks 5-6**: Implement controller actions (GET/POST)
4. **Tasks 7-8**: Create view with JavaScript state management
5. **Tasks 9-12**: Wire up redirects and update integrations
6. **Task 13**: Manual testing
7. **Task 14**: Cleanup (optional, post-validation)

Each task follows TDD principles where applicable and includes verification steps and commits.

---

**Plan complete and saved to `docs/plans/2026-01-29-unified-send-wizard-implementation.md`. Two execution options:**

**1. Subagent-Driven (this session)** - I dispatch fresh subagent per task, review between tasks, fast iteration

**2. Parallel Session (separate)** - Open new session with executing-plans, batch execution with checkpoints

**Which approach?**
