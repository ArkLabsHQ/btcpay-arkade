using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NArk.Abstractions.Extensions;


namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

public partial class ArkGreenfieldController
{
    #region Server Info

    /// <summary>
    /// Get Ark operator server information.
    /// </summary>
    [HttpGet("~/api/v1/stores/{storeId}/arkade/server-info")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> GetServerInfo(string storeId, CancellationToken cancellationToken)
    {
        var (_, error) = GetStoreConfig();
        if (error != null) return error;

        try
        {
            var info = await clientTransport.GetServerInfoAsync(cancellationToken);
            return Ok(new ArkServerInfoData
            {
                Network = info.Network.Name,
                DustSats = info.Dust.Satoshi,
                SignerPubKey = Convert.ToHexString(info.SignerKey.Extract().XOnlyPubKey.ToBytes()).ToLowerInvariant(),
                UnilateralExitBlocks = (int)info.UnilateralExit.Value,
                BoardingExitBlocks = (int)info.BoardingExit.Value,
                ForfeitAddress = info.ForfeitAddress?.ToString()
            });
        }
        catch (Exception ex)
        {
            return this.CreateAPIError(503, "operator-unavailable",
                $"Cannot reach Ark operator: {ex.Message}");
        }
    }

    #endregion

    #region Status

    /// <summary>
    /// Get overall Arkade service status for a store.
    /// </summary>
    [HttpGet("~/api/v1/stores/{storeId}/arkade/status")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> GetStatus(string storeId, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store == null) return NotFound();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);
        var status = new ArkStatusData
        {
            IsConfigured = config?.WalletId != null
        };

        // Check Ark operator
        try
        {
            await clientTransport.GetServerInfoAsync(cancellationToken);
            status.ArkOperator = new ArkServiceConnectionData
            {
                Url = arkNetworkConfig.ArkUri,
                IsConnected = true
            };
        }
        catch (Exception ex)
        {
            status.ArkOperator = new ArkServiceConnectionData
            {
                Url = arkNetworkConfig.ArkUri,
                IsConnected = false,
                Error = ex.Message
            };
        }

        // The Arkade swap solver. Nothing is dialled: both sides of the RFQ transport dial out and
        // neither listens, so IsConnected reports that a solver is configured to reach, which is the
        // strongest claim available without opening a negotiation to answer a status call.
        status.Solver = new ArkServiceConnectionData
        {
            Url = arkadeSolver.RelayUri,
            IsConnected = arkadeSolver.IsConfigured,
            Error = arkadeSolver.IsConfigured
                ? null
                : "No Arkade swap solver can be reached: this network publishes no solver registry "
                  + "and none is named in ark.json ('solver-relay' and 'solver-pubkey')."
        };

        // Blockchain info
        try
        {
            var (timestamp, height) = await bitcoinTimeChainProvider.GetChainTime(cancellationToken);
            status.Blockchain = new ArkBlockchainData
            {
                Height = height,
                Timestamp = timestamp
            };
        }
        catch
        {
            // Skip blockchain info if unavailable
        }

        return Ok(status);
    }

    #endregion

    #region Sync

    /// <summary>
    /// Trigger a wallet sync (VTXOs + boarding UTXOs).
    /// </summary>
    [HttpPost("~/api/v1/stores/{storeId}/arkade/sync")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> SyncWallet(string storeId, CancellationToken cancellationToken)
    {
        var (config, error) = GetStoreConfig();
        if (error != null) return error;

        try
        {
            var contracts = await contractStorage.GetContracts(
                walletIds: [config!.WalletId!], cancellationToken: cancellationToken);
            await vtxoSyncService.PollScriptsForVtxos(
                contracts.Select(c => c.Script).ToHashSet(), cancellationToken);
            await boardingUtxoSyncService.SyncAsync(cancellationToken);
            return Ok(new { synced = true });
        }
        catch (Exception ex)
        {
            return this.CreateAPIError("sync-failed", $"Sync failed: {ex.Message}");
        }
    }

    #endregion
}
