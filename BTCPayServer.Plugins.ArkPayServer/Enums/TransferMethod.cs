
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.ArkPayServer.Enums;

public enum TransferMethod
{
    [Display(Name = "On-chain")]
    Onchain,
    [Display(Name = "Lightning")]
    Lightning,
    [Display(Name = "Ark")]
    Ark
}