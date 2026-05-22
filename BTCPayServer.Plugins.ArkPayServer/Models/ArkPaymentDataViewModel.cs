namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class ArkPaymentDataViewModel
{
    public string Outpoint { get; set; }
    public string Amount { get; set; }
    public string Contract { get; set; }
    public string Address { get; set; }
    public DateTimeOffset ReceivedTime { get; set; }
    public string Currency { get; set; }
    public bool IsBoarding { get; set; }

    /// <summary>
    /// True for a boarding payment whose on-chain funding tx is still
    /// unconfirmed (payment status Processing). Such a payment is not yet
    /// spendable or settleable.
    /// </summary>
    public bool IsUnconfirmed { get; set; }
}