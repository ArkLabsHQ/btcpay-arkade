using AsyncKeyedLock;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Common;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Payouts;
using BTCPayServer.Plugins.ArkPayServer.Cache;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Exceptions;
using BTCPayServer.Plugins.ArkPayServer.Models.Events;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Notifications;
using BTCPayServer.Services.Notifications.Blobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NArk;
using NArk.Extensions;
using NArk.Services.Abstractions;
using NBitcoin;
using NBitcoin.Payment;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PayoutData = BTCPayServer.Data.PayoutData;
using StoreData = BTCPayServer.Data.StoreData;

namespace BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;

public class ArkPayoutHandler(
    ILogger<ArkPayoutHandler> logger,
    IOperatorTermsService operatorTermsService,
    EventAggregator eventAggregator,
    PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
    ApplicationDbContextFactory dbContextFactory,
    BTCPayNetworkJsonSerializerSettings jsonSerializerSettings,
    BTCPayNetworkProvider networkProvider,
    TrackedContractsCache trackedContractsCache,
    NotificationSender notificationSender,
        ArkConfiguration arkConfiguration
    
) : IPayoutHandler, IHasNetwork
{
    public AsyncKeyedLock.AsyncKeyedLocker<string> PayoutLocker = new AsyncKeyedLocker<string>();
    
    public string Currency => "BTC";
    public PayoutMethodId PayoutMethodId => ArkadePlugin.ArkadePayoutMethodId;

    public bool IsSupported(StoreData storeData)
    {
        var config =
            storeData
                .GetPaymentMethodConfig<ArkadePaymentMethodConfig>(
                    ArkadePlugin.ArkadePaymentMethodId,
                    paymentMethodHandlerDictionary,
                    true
                );

        return !string.IsNullOrWhiteSpace(config?.WalletId) && config.GeneratedByStore;
    }

    public Task TrackClaim(ClaimRequest claimRequest, PayoutData payoutData)
    {
        trackedContractsCache.TriggerUpdate();
        return Task.CompletedTask;
    }

    public async Task<(IClaimDestination destination, string error)> ParseClaimDestination(string destination,
        CancellationToken cancellationToken)
    {
        destination = destination.Trim();
        try
        {
            var terms = await operatorTermsService.GetOperatorTerms(cancellationToken);

            if (destination.StartsWith("bitcoin:", StringComparison.InvariantCultureIgnoreCase))
            {
                return (new ArkUriClaimDestination(new BitcoinUrlBuilder(destination, terms.Network)), null!);
            }

            return (
                new ArkAddressClaimDestination(ArkAddress.Parse(destination),
                    terms.Network.ChainName == ChainName.Mainnet), null!);
        }
        catch
        {
            return (null!, "A valid address was not provided");
        }
    }

    public (bool valid, string? error) ValidateClaimDestination(IClaimDestination claimDestination,
        PullPaymentBlob? pullPaymentBlob)
    {
        return (true, null);
    }

    public IPayoutProof ParseProof(PayoutData? payout)
    {
        if (payout?.Proof is null)
            return null!;
        var payoutMethodId = payout.GetPayoutMethodId();
        if (payoutMethodId is null)
            return null!;

        var parseResult = ParseProofType(payout.Proof);
        if (parseResult is null)
            return null!;
        
        if (parseResult.Value.MaybeType == ArkPayoutProof.Type)
        {
            var res = parseResult.Value.Object.ToObject<ArkPayoutProof>(
                JsonSerializer.Create(jsonSerializerSettings.GetSerializer(payoutMethodId))
            )!;
            
            res.Link = $"{arkConfiguration.ArkUri}/v1/indexer/vtxos?scripts={ArkAddress.Parse(payout.DedupId).ScriptPubKey.ToHex()}";
            return res;
        }

        return parseResult.Value.Object.ToObject<ManualPayoutProof>()!;
    }

    private static (JObject Object, string? MaybeType)? ParseProofType(string? proof)
    {
        if (proof is null)
        {
            return null;
        }

        var obj = JObject.Parse(proof);
        var type = TryParseProofType(obj);

        
        return (obj, type);
    }

    private static string? TryParseProofType(JObject? proof)
    {
        if (proof is null) return null;

        if (!proof.TryGetValue("proofType", StringComparison.InvariantCultureIgnoreCase, out var proofType))
            return null;
        
        return proofType.Value<string>();
    }

    public void StartBackgroundCheck(Action<Type[]> subscribe)
    {
        subscribe([typeof(VTXOsUpdated)]);
    }

    public async Task BackgroundCheck(object o)
    {
        if (o is VTXOsUpdated vtxoEvent)
        {

            var terms = await operatorTermsService.GetOperatorTerms();
            var newVtxos = vtxoEvent.Vtxos.Where(vtxo => vtxo.SpentByTransactionId is null)
                .GroupBy(vtxo => vtxo.Script).ToDictionary(g => ArkAddress.FromScriptPubKey(Script.FromHex(g.Key), terms.SignerKey.ToXOnlyPubKey()).ToString(terms.Network.ChainName == ChainName.Mainnet), g => g.ToArray());           
            
            var addresses = newVtxos.Keys.ToArray();
            
            await using var ctx = dbContextFactory.CreateContext();
            var payouts = await ctx.Payouts
                .Include(o => o.StoreData)
                .Include(o => o.PullPaymentData)
                .Where(p => p.State == PayoutState.AwaitingPayment)
                .Where(p => p.PayoutMethodId == PayoutMethodId.ToString())
                .Where(p => addresses.Contains(p.DedupId))
                .ToListAsync();

            foreach (var payout in payouts)
            {
                if (PayoutLocker.LockOrNullAsync(payout.Id, 0) is var locker && await locker is { } disposable)
                {
                    using (disposable)
                    {


                        if (!newVtxos.TryGetValue(payout.DedupId, out var matched))
                        {
                            continue;
                        }

                        if (payout.Amount is null || matched.All(vtxo =>
                                Money.Satoshis(vtxo.Amount).ToDecimal(MoneyUnit.BTC) != payout.Amount))
                        {
                            continue;
                        }

                        var txId = matched
                            .First(vtxo => Money.Satoshis(vtxo.Amount).ToDecimal(MoneyUnit.BTC) == payout.Amount)
                            .TransactionId;
                        SetProofBlob(payout,
                            new ArkPayoutProof {TransactionId = uint256.Parse(txId), DetectedInBackground = true,});
                        await notificationSender.SendNotification(new StoreScope(payout.StoreDataId),
                            new ExternalPayoutTransactionNotification()
                            {
                                PaymentMethod = payout.PayoutMethodId,
                                PayoutId = payout.Id,
                                StoreId = payout.StoreDataId
                            });
                        await ctx.SaveChangesAsync();
                        eventAggregator.Publish(new PayoutEvent(PayoutEvent.PayoutEventType.Updated, payout));
                    }
                }
            }
        }
    }

    public async Task<decimal> GetMinimumPayoutAmount(IClaimDestination claimDestination)
    {
        var terms = await operatorTermsService.GetOperatorTerms();
        return terms.Dust.ToDecimal(MoneyUnit.BTC);
    }

    public Dictionary<PayoutState, List<(string Action, string Text)>> GetPayoutSpecificActions()
    {
        return new Dictionary<PayoutState, List<(string Action, string Text)>>()
        {
            {PayoutState.AwaitingPayment, new List<(string Action, string Text)>()
            {
                ("reject-payment", "Reject payout transaction")
                
            }},
            
        };
    }

    public async Task<StatusMessageModel> DoSpecificAction(string action, string[] payoutIds, string storeId)
    {
        switch (action)
        {
            case "mark-paid":
                await using (var context = dbContextFactory.CreateContext())
                {
                    var payouts = (await PullPaymentHostedService.GetPayouts(new PullPaymentHostedService.PayoutQuery()
                        {
                            States = [PayoutState.AwaitingPayment],
                            Stores = [storeId],
                            PayoutIds = payoutIds
                        }, context)).Where(data =>
                            PayoutMethodId.TryParse(data.PayoutMethodId, out var payoutMethodId) &&
                            payoutMethodId == PayoutMethodId)
                        .Select(data => (data, ParseProof(data) as ArkPayoutProof)).Where(tuple => tuple.Item2 is
                        {
                            DetectedInBackground: false
                        });
                    foreach (var valueTuple in payouts)
                    {
                        valueTuple.data.State = PayoutState.Completed;
                    }

                    await context.SaveChangesAsync();
                }

                return new StatusMessageModel
                {
                    Message = "Payout payments have been marked confirmed",
                    Severity = StatusMessageModel.StatusSeverity.Success
                };
            case "reject-payment":
                await using (var context = dbContextFactory.CreateContext())
                {
                    var payouts = (await PullPaymentHostedService.GetPayouts(new PullPaymentHostedService.PayoutQuery()
                        {
                            States = [PayoutState.AwaitingPayment],
                            Stores = [storeId],
                            PayoutIds = payoutIds
                        }, context)).Where(data =>
                            PayoutMethodId.TryParse(data.PayoutMethodId, out var payoutMethodId) &&
                            payoutMethodId == PayoutMethodId)
                        .Select(data => (data, ParseProof(data) as ArkPayoutProof)).Where(tuple => tuple.Item2 is
                        {
                            DetectedInBackground: true
                        });
                    foreach (var valueTuple in payouts)
                    {
                        SetProofBlob(valueTuple.data, null);
                    }

                    await context.SaveChangesAsync();
                }

                return new StatusMessageModel()
                {
                    Message = "Payout payments have been unmarked",
                    Severity = StatusMessageModel.StatusSeverity.Success
                };
        }

        return null;
    }

    public async Task<IActionResult> InitiatePayment(string[] payoutIds)
    {
        var terms = await operatorTermsService.GetOperatorTerms();

        await using var ctx = dbContextFactory.CreateContext();
        ctx.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        var payouts = await ctx.Payouts
            .Include(data => data.PullPaymentData)
            .Where(data => payoutIds.Contains(data.Id)
                           && PayoutMethodId.ToString() == data.PayoutMethodId
                           && data.State == PayoutState.AwaitingPayment)
            .ToListAsync();

        var storeId = payouts.First().StoreDataId;

        List<string> bip21s = [];

        foreach (var payout in payouts)
        {
            var blob = payout.GetBlob(jsonSerializerSettings);
            if (payout.GetPayoutMethodId() != PayoutMethodId)
                continue;
            var claim = await ParseClaimDestination(blob.Destination, CancellationToken.None);
            var bip21 = await TryGenerateBip21(payout, claim);
            if (bip21 is not null)
                bip21s.Add(bip21);
        }

        return new RedirectToActionResult("SpendOverview", "Ark", new { storeId = storeId, destinations = bip21s });
    }

    public async Task<string?> TryGenerateBip21(PayoutData payout, (IClaimDestination destination, string error) claim)
    {
        var terms = await operatorTermsService.GetOperatorTerms();
        switch (claim.destination)
        {
            case ArkUriClaimDestination uriClaimDestination:
                uriClaimDestination.BitcoinUrl.Amount = new Money(payout.Amount.Value, MoneyUnit.BTC);
                var newUri = new UriBuilder(uriClaimDestination.BitcoinUrl.Uri);
                BTCPayServerClient.AppendPayloadToQuery(newUri,
                    new KeyValuePair<string, object>("payout", payout.Id));
                return newUri.Uri.ToString();
            case ArkAddressClaimDestination addressClaimDestination:
                var builder = new PaymentUrlBuilder("bitcoin")
                {
                    Host = addressClaimDestination.Address.ToString(terms.Network.ChainName == ChainName.Mainnet)
                };
                builder.QueryParams.Add("amount", payout.Amount.Value.ToString());
                builder.QueryParams.Add("payout", payout.Id);
                return builder.ToString();
            default:
                return null;
        }
    }

    public BTCPayNetwork Network => networkProvider.GetNetwork<BTCPayNetwork>(Currency);
    
    public void SetProofBlob(PayoutData data, ArkPayoutProof blob)
    {
         data.SetProofBlob(blob, jsonSerializerSettings.GetSerializer(data.GetPayoutMethodId()));
    }
    
    public JObject SerializeProof(ArkPayoutProof arkPayoutProof)
    {
        var serializer = JsonSerializer.Create(jsonSerializerSettings.GetSerializer(PayoutMethodId));
        return JObject.FromObject(arkPayoutProof, serializer);
    }
}