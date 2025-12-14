using NArk;
using NArk.Contracts;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public record ArkadePromptDetails(
    string WalletId,
    string Contract);
    