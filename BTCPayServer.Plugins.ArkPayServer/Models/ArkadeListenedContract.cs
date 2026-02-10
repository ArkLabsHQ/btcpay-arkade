using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

internal record ArkadeListenedContract(ArkadePromptDetails Details, string InvoiceId);