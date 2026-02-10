using BTCPayServer.Plugins.Arkade.PaymentHandler;

namespace BTCPayServer.Plugins.Arkade.Models;

internal record ArkadeListenedContract(ArkadePromptDetails Details, string InvoiceId);