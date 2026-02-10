namespace BTCPayServer.Plugins.ArkPayServer.Models;

using BTCPayServer.Plugins.ArkPayServer.Data.Entities;

public class ArkStoreWalletViewModel
{
    public string? WalletId { get; set; }
    public string? Destination { get; set; }

    public bool SignerAvailable { get; set; }
    public Dictionary<ArkWalletContract, VTXO[]>? Contracts { get; set; }
    public bool LNEnabled { get; set; }

    public string? Wallet { get; set; }
}