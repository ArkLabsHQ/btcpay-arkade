using AsyncKeyedLock;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Common;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Payouts;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Notifications;
using BTCPayServer.Services.Notifications.Blobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Scripts;
using NArk.Abstractions.VTXOs;
using NArk.Hosting;
using NArk.Core.Transport;
using NArk.Swaps.Helpers;
using NBitcoin;
using NBitcoin.Payment;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PayoutData = BTCPayServer.Data.PayoutData;
using StoreData = BTCPayServer.Data.StoreData;

namespace BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;

public class ArkPayoutHandler : IPayoutHandler, IHasNetwork, IActiveScriptsProvider
{
    private readonly ILogger<ArkPayoutHandler> _logger;
    private readonly IClientTransport _clientTransport;
    private readonly EventAggregator _eventAggregator;
    private readonly PaymentMethodHandlerDictionary _paymentMethodHandlerDictionary;
    private readonly ApplicationDbContextFactory _dbContextFactory;
    private readonly BTCPayNetworkJsonSerializerSettings _jsonSerializerSettings;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly NotificationSender _notificationSender;
    private readonly ArkNetworkConfig _arkNetworkConfig;
    private readonly IVtxoStorage _vtxoStorage;

    public ArkPayoutHandler(
        ILogger<ArkPayoutHandler> logger,
        IClientTransport clientTransport,
        EventAggregator eventAggregator,
        PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
        ApplicationDbContextFactory dbContextFactory,
        BTCPayNetworkJsonSerializerSettings jsonSerializerSettings,
        BTCPayNetworkProvider networkProvider,
        NotificationSender notificationSender,
        ArkNetworkConfig arkNetworkConfig,
        IVtxoStorage vtxoStorage)
    {
        _logger = logger;
        _clientTransport = clientTransport;
        _eventAggregator = eventAggregator;
        _paymentMethodHandlerDictionary = paymentMethodHandlerDictionary;
        _dbContextFactory = dbContextFactory;
        _jsonSerializerSettings = jsonSerializerSettings;
        _networkProvider = networkProvider;
        _notificationSender = notificationSender;
        _arkNetworkConfig = arkNetworkConfig;
        _vtxoStorage = vtxoStorage;

        // Subscribe directly to NNark's VTXO storage events
        _vtxoStorage.VtxosChanged += OnVtxoChanged;
    }
    public readonly AsyncKeyedLocker<string> PayoutLocker = new();
    
    public string Currency => "BTC";
    public PayoutMethodId PayoutMethodId => ArkadePlugin.ArkadePayoutMethodId;

    public bool IsSupported(StoreData storeData)
    {
        var config =
            storeData
                .GetPaymentMethodConfig<ArkadePaymentMethodConfig>(
                    ArkadePlugin.ArkadePaymentMethodId,
                    _paymentMethodHandlerDictionary,
                    true
                );

        return !string.IsNullOrWhiteSpace(config?.WalletId) && config.GeneratedByStore;
    }

    public Task TrackClaim(ClaimRequest claimRequest, PayoutData payoutData)
    {
        return Task.CompletedTask;
    }

    public async Task<(IClaimDestination destination, string error)> ParseClaimDestination(string destination,
        CancellationToken cancellationToken)
    {
        destination = destination.Trim();
        try
        {
            var terms = await _clientTransport.GetServerInfoAsync(cancellationToken);

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
                JsonSerializer.Create(_jsonSerializerSettings.GetSerializer(payoutMethodId))
            )!;

            res.Link = $"{_arkNetworkConfig.ArkUri}/v1/indexer/vtxos?scripts={ArkAddress.Parse(payout.DedupId).ScriptPubKey.ToHex()}";
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
        subscribe([typeof(PayoutEvent)]);
    }

    public Task BackgroundCheck(object o)
    {
        if (o is PayoutEvent payoutEvent && payoutEvent.Payout.PayoutMethodId == PayoutMethodId.ToString())
        {
            ActiveScriptsChanged?.Invoke(this, EventArgs.Empty);
        }
        return Task.CompletedTask;
    }

    private async void OnVtxoChanged(object? sender, ArkVtxo vtxo)
    {
        try
        {
            // Skip spent VTXOs
            if (vtxo.SpentByTransactionId is not null)
                return;

            var terms = await _clientTransport.GetServerInfoAsync();
            var serverKey = terms.SignerKey.Extract().XOnlyPubKey;
            var address = ArkAddress.FromScriptPubKey(Script.FromHex(vtxo.Script), serverKey)
                .ToString(terms.Network.ChainName == ChainName.Mainnet);

            await using var ctx = _dbContextFactory.CreateContext();
            var payout = await ctx.Payouts
                .Include(p => p.StoreData)
                .Include(p => p.PullPaymentData)
                .Where(p => p.State == PayoutState.AwaitingPayment)
                .Where(p => p.PayoutMethodId == PayoutMethodId.ToString())
                .Where(p => p.DedupId == address)
                .FirstOrDefaultAsync();

            if (payout is null)
                return;

            if (PayoutLocker.LockOrNullAsync(payout.Id, 0) is var locker && await locker is { } disposable)
            {
                using (disposable)
                {
                    // Check if amount matches
                    var vtxoAmount = Money.Satoshis(vtxo.Amount).ToDecimal(MoneyUnit.BTC);
                    if (payout.Amount is null || vtxoAmount != payout.Amount)
                        return;

                    SetProofBlob(payout,
                        new ArkPayoutProof { TransactionId = uint256.Parse(vtxo.TransactionId), DetectedInBackground = true });
                    await _notificationSender.SendNotification(new StoreScope(payout.StoreDataId),
                        new ExternalPayoutTransactionNotification()
                        {
                            PaymentMethod = payout.PayoutMethodId,
                            PayoutId = payout.Id,
                            StoreId = payout.StoreDataId
                        });
                    await ctx.SaveChangesAsync();
                    _eventAggregator.Publish(new PayoutEvent(PayoutEvent.PayoutEventType.Updated, payout));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling VTXO change for payout detection");
        }
    }

    public async Task<decimal> GetMinimumPayoutAmount(IClaimDestination claimDestination)
    {
        var terms = await _clientTransport.GetServerInfoAsync();
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
                await using (var context = _dbContextFactory.CreateContext())
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
                await using (var context = _dbContextFactory.CreateContext())
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
        var terms = await _clientTransport.GetServerInfoAsync();

        await using var ctx = _dbContextFactory.CreateContext();
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
            var blob = payout.GetBlob(_jsonSerializerSettings);
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
        var terms = await _clientTransport.GetServerInfoAsync();
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

    public BTCPayNetwork Network => _networkProvider.GetNetwork<BTCPayNetwork>(Currency);

    public void SetProofBlob(PayoutData data, ArkPayoutProof? blob)
    {
        data.SetProofBlob(blob, _jsonSerializerSettings.GetSerializer(data.GetPayoutMethodId()));
    }

    public JObject SerializeProof(ArkPayoutProof arkPayoutProof)
    {
        var serializer = JsonSerializer.Create(_jsonSerializerSettings.GetSerializer(PayoutMethodId));
        return JObject.FromObject(arkPayoutProof, serializer);
    }

    public event EventHandler? ActiveScriptsChanged;

    public async Task<HashSet<string>> GetActiveScripts(CancellationToken cancellationToken = default)
    {
        //load all scripts of payouts with arkade payment method and are in AwaitingPayment or Processing state
        await using var ctx = _dbContextFactory.CreateContext();
        ctx.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        var payouts = await ctx.Payouts
            .Where(data => PayoutMethodId.ToString() == data.PayoutMethodId
                           && (data.State == PayoutState.AwaitingPayment || data.State == PayoutState.InProgress) 
                           && data.DedupId != null)
            .Select(data => data.DedupId)
            .Distinct().ToListAsync(cancellationToken);

        return payouts.ToHashSet()!;
    }
}