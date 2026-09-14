using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BTCPayServer.Plugins.ArkPayServer.Controllers;

internal sealed class ArkEvmSettlementValidationAttribute : ActionFilterAttribute
{
    public ArkEvmSettlementValidationAttribute() => Order = -3000;

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        // Run before ApiController's model-state filter, which can echo secret conversion inputs.
        if (!context.ModelState.IsValid) context.Result = InvalidSettings();
    }

    internal static BadRequestObjectResult InvalidSettings() => new(new
    {
        code = "invalid-settlement-settings",
        message = "Specify valid settlement settings and a complete policy before enabling."
    });
}
