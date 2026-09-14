using System.ComponentModel.DataAnnotations;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.ArkPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.Models.Api.Greenfield;
using BTCPayServer.Plugins.ArkPayServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using NArk.ArkadeIntents;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

/// <summary>Owner-authorized public route index; swap state is projected live from SDK intent storage.</summary>
/// <param name="repository">Store-scoped route index.</param>
[ApiController]
[Authorize(Policy = Policies.CanViewInvoices, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[EnableCors(CorsPolicies.All)]
public sealed class ArkCompositionRoutesController(ArkCompositionRouteRepository repository,
    IArkadeIntentStorage intents, IArkCompositionExecutionLock? executionLock = null) : ControllerBase
{
    /// <summary>Reads one route without revealing whether another store owns it.</summary>
    [HttpGet("~/api/v1/stores/{storeId}/arkade/evm-settlement/routes/{routeId:guid}")]
    public async Task<IActionResult> Get(string storeId, Guid routeId, CancellationToken cancellationToken)
    {
        if (!OwnsStore(storeId)) return NotFound();
        var route = await repository.Get(storeId, routeId, cancellationToken);
        return route is null ? NotFound() : Ok(await ProjectAsync(route, cancellationToken));
    }

    /// <summary>Lists bounded independent routes, including unattached prompts and renewals.</summary>
    [HttpGet("~/api/v1/stores/{storeId}/arkade/evm-settlement/routes")]
    public async Task<IActionResult> List(string storeId, CancellationToken cancellationToken,
        [FromQuery, StringLength(128)] string? invoiceId = null, [FromQuery] string? paymentMethodId = null,
        [FromQuery, Range(0, int.MaxValue)] int skip = 0, [FromQuery, Range(1, 100)] int take = 50)
    {
        if (!OwnsStore(storeId)) return NotFound();
        if (paymentMethodId is not (null or "ARKADE" or "BTC-LN" or "BTC-CHAIN"))
            return BadRequest(new { code = "invalid-payment-method", message = "Specify a supported source payment method." });
        var routes = await repository.List(storeId, invoiceId, paymentMethodId, skip, take, cancellationToken);
        var projected = new List<ArkCompositionRouteData>();
        foreach (var route in routes) projected.Add(await ProjectAsync(route, cancellationToken));
        return Ok(projected.ToArray());
    }

    private async Task<ArkCompositionRouteData> ProjectAsync(ArkCompositionRoute route,
        CancellationToken cancellationToken) => ArkCompositionRouteData.From(route,
        await intents.GetArkadeSwapIntent(route.OutgoingSwapId, cancellationToken),
        route.IngressSwapId is null ? null : await intents.GetArkadeSwapIntent(route.IngressSwapId, cancellationToken),
        ExecutionAvailable);

    private bool OwnsStore(string storeId) => HttpContext.GetStoreDataOrNull() is { } store &&
                                              string.Equals(store.Id, storeId, StringComparison.Ordinal);
    private bool ExecutionAvailable => executionLock?.SupportsCrossProcessExecution == true;
}
