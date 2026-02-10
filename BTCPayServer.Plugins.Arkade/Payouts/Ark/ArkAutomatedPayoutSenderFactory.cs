using BTCPayServer.Data;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.Payouts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.Arkade.Payouts.Ark;

public class ArkAutomatedPayoutSenderFactory(IServiceProvider serviceProvider, LinkGenerator linkGenerator): IPayoutProcessorFactory
{
    public string Processor => nameof(ArkAutomatedPayoutSenderFactory);
    public string FriendlyName => "Automated Ark Sender";
    public string ConfigureLink(string storeId, PayoutMethodId payoutMethodId, HttpRequest request)
    {
        return linkGenerator.GetUriByAction("ConfigurePayoutProcessor",
            "Ark", new
            {
                storeId
            }, request.Scheme, request.Host, request.PathBase) ?? string.Empty;
    }

    public IEnumerable<PayoutMethodId> GetSupportedPayoutMethods()
    {
        return [ArkadePlugin.ArkadePayoutMethodId];
    }

    public ArkAutomatedPayoutProcessor ConstructProcessor(PayoutProcessorData settings)
    {
        if (settings.Processor != Processor)
        {
            throw new NotSupportedException("This processor cannot handle the provided requirements");
        }
        
        return ActivatorUtilities.CreateInstance<ArkAutomatedPayoutProcessor>(serviceProvider, settings);
    }
    Task<IHostedService> IPayoutProcessorFactory.ConstructProcessor(PayoutProcessorData settings)
    {
        return Task.FromResult<IHostedService>(ConstructProcessor(settings));
    }
}