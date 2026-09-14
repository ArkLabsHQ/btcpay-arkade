using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public sealed record ArkPaymentProofDisplay(bool IsEvmSettlement, string Identifier)
{
    public static ArkPaymentProofDisplay From(ArkadePaymentData payment, JToken? details)
    {
        var route = details?["compositionRouteId"];
        var claim = details?["evmClaimTransactionId"];
        if (route is null && claim is null && !payment.Outpoint.StartsWith("evm:", StringComparison.Ordinal))
            return new(false, payment.Outpoint);

        var transactionId = claim is { Type: JTokenType.String } ? claim.Value<string>() : null;
        if (transactionId is { Length: 66 } && transactionId.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            transactionId.Skip(2).All(char.IsAsciiHexDigit))
            return new(true, transactionId);
        if (route is { Type: JTokenType.String } && Guid.TryParse(route.Value<string>(), out var routeId))
            return new(true, $"Route {routeId:N}");
        return new(true, "Unavailable");
    }
}
