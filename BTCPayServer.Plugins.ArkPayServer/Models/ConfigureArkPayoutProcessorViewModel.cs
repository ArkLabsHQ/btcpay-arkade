using System.ComponentModel.DataAnnotations;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class ConfigureArkPayoutProcessorViewModel
{
    public ConfigureArkPayoutProcessorViewModel()
    {
    }

    public ConfigureArkPayoutProcessorViewModel(ArkAutomatedPayoutBlob blob)
    {
        IntervalMinutes = blob.Interval.TotalMinutes;
        ProcessNewPayoutsInstantly = blob.ProcessNewPayoutsInstantly;
    }
    [Display(Name = "Process approved payouts instantly")]
    public bool ProcessNewPayoutsInstantly { get; set; }

    [Range(AutomatedPayoutConstants.MinIntervalMinutes, AutomatedPayoutConstants.MaxIntervalMinutes)]
    public double IntervalMinutes { get; set; }

    public ArkAutomatedPayoutBlob ToBlob()
    {
        return new ArkAutomatedPayoutBlob {
            ProcessNewPayoutsInstantly = ProcessNewPayoutsInstantly,
            Interval = TimeSpan.FromMinutes(IntervalMinutes), 
        };
    }
}