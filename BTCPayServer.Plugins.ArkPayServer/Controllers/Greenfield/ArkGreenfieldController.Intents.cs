using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NArk.Abstractions.Intents;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

public partial class ArkGreenfieldController
{
    #region Intents

    /// <summary>
    /// List intents (pending transactions) for the store's wallet.
    /// </summary>
    [HttpGet("~/api/v1/stores/{storeId}/arkade/intents")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> ListIntents(string storeId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        [FromQuery] string? state = null,
        CancellationToken cancellationToken = default)
    {
        var (config, error) = GetStoreConfig();
        if (error != null) return error;

        take = Math.Min(take, 500);

        ArkIntentState[]? stateFilter = null;
        if (!string.IsNullOrEmpty(state) && Enum.TryParse<ArkIntentState>(state, true, out var parsedState))
            stateFilter = [parsedState];

        var intents = await intentStorage.GetIntents(
            walletIds: [config!.WalletId!],
            skip: skip,
            take: take,
            states: stateFilter,
            cancellationToken: cancellationToken);

        var result = intents.Select(i => new ArkIntentData
        {
            IntentId = i.IntentId,
            IntentTxId = i.IntentTxId,
            WalletId = i.WalletId,
            State = i.State.ToString(),
            CreatedAt = i.CreatedAt,
            ValidFrom = i.ValidFrom,
            ValidUntil = i.ValidUntil,
            BatchId = i.BatchId,
            CommitmentTransactionId = i.CommitmentTransactionId,
            CancellationReason = i.CancellationReason,
            Vtxos = i.IntentVtxos?.Select(v => new ArkIntentVtxoData
            {
                Outpoint = v.ToString()
            }).ToList() ?? new()
        }).ToList();

        return Ok(result);
    }

    /// <summary>
    /// Cancel a pending intent.
    /// </summary>
    [HttpDelete("~/api/v1/stores/{storeId}/arkade/intents/{intentTxId}")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> CancelIntent(string storeId, string intentTxId,
        CancellationToken cancellationToken)
    {
        var (config, error) = GetStoreConfig();
        if (error != null) return error;

        try
        {
            var intents = await intentStorage.GetIntents(
                walletIds: [config!.WalletId!],
                intentTxIds: [intentTxId],
                cancellationToken: cancellationToken);

            var intent = intents.FirstOrDefault();
            if (intent == null)
                return this.CreateAPIError(404, "intent-not-found", "Intent not found.");

            // If intent was submitted to server, delete from server
            if (intent.State == ArkIntentState.WaitingForBatch)
            {
                try { await clientTransport.DeleteIntent(intent, cancellationToken); }
                catch { /* Continue — we still mark cancelled in storage */ }
            }

            // Update storage to mark as cancelled
            await intentStorage.SaveIntent(intent.WalletId, intent with
            {
                State = ArkIntentState.Cancelled,
                CancellationReason = "Cancelled via API",
                UpdatedAt = DateTimeOffset.UtcNow
            }, cancellationToken);

            return Ok();
        }
        catch (Exception ex)
        {
            return this.CreateAPIError("cancel-failed", $"Failed to cancel intent: {ex.Message}");
        }
    }

    #endregion

    #region Contracts

    /// <summary>
    /// List contracts (address derivations) for the store's wallet.
    /// </summary>
    [HttpGet("~/api/v1/stores/{storeId}/arkade/contracts")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> ListContracts(string storeId,
        [FromQuery] bool activeOnly = false,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        var (config, error) = GetStoreConfig();
        if (error != null) return error;

        take = Math.Min(take, 500);

        var contracts = await contractStorage.GetContracts(
            walletIds: [config!.WalletId!],
            isActive: activeOnly ? true : null,
            cancellationToken: cancellationToken);

        // Get VTXO counts per contract script
        var unspentVtxos = await vtxoStorage.GetVtxos(
            walletIds: [config.WalletId!],
            includeSpent: false,
            cancellationToken: cancellationToken);
        var vtxoCountByScript = unspentVtxos
            .GroupBy(v => v.Script)
            .ToDictionary(g => g.Key, g => g.Count());

        var result = contracts
            .Skip(skip)
            .Take(take)
            .Select(c => new ArkContractData
            {
                Script = c.Script,
                WalletId = c.WalletIdentifier,
                ContractType = c.Type,
                ActivityState = c.ActivityState.ToString(),
                CreatedAt = c.CreatedAt,
                Metadata = c.Metadata,
                VtxoCount = vtxoCountByScript.GetValueOrDefault(c.Script)
            }).ToList();

        return Ok(result);
    }

    #endregion
}
