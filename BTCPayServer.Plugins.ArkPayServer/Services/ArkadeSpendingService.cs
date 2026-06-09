using System.Globalization;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Exceptions;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkadeSpendingService(
    ISpendingService arkadeSpender,
    IClientTransport clientTransport,
    VtxoSynchronizationService vtxoSyncService,
    IContractStorage contractStorage,
    PaymentMethodHandlerDictionary paymentMethodHandlerDictionary)
{
    /// <summary>
    /// Spend funds from the store's Arkade wallet to a destination.
    /// </summary>
    /// <param name="store">Store whose Arkade wallet should be used.</param>
    /// <param name="destination">
    /// Destination string. Supported formats: bare Ark address, BIP21 URI (with <c>ark</c> query parameter
    /// or Ark address host), Lightning BOLT11 invoice (optionally prefixed with <c>lightning:</c>).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// On-chain Ark transaction ID for non-Lightning payments. For Lightning payments returns <c>null</c>
    /// because the payment hash is not surfaced through the current Lightning client API.
    /// </returns>
    public Task<string?> Spend(StoreData store, string destination, CancellationToken cancellationToken)
        => Spend(store, destination, amountSats: null, inputOutpoints: null, cancellationToken);

    /// <summary>
    /// Spend funds from the store's Arkade wallet to a destination, with explicit amount and/or coin selection.
    /// </summary>
    /// <param name="store">Store whose Arkade wallet should be used.</param>
    /// <param name="destination">
    /// Destination string. Supported formats: bare Ark address, BIP21 URI (with <c>ark</c> query parameter
    /// or Ark address host), Lightning BOLT11 invoice (optionally prefixed with <c>lightning:</c>).
    /// </param>
    /// <param name="amountSats">
    /// Optional amount in satoshis. When provided, overrides any amount embedded in the destination
    /// (e.g. BIP21 <c>amount</c> query parameter). Required for bare Ark addresses unless the address is
    /// embedded inside a BIP21 URI with an amount. Must be omitted for Lightning destinations because the
    /// amount is fixed by the BOLT11 invoice.
    /// </param>
    /// <param name="inputOutpoints">
    /// Optional list of VTXO outpoints (in <c>txid:vout</c> form) to spend. When provided, the wallet's
    /// automatic coin selection is bypassed and only the specified coins are used as inputs. Must be
    /// omitted for Lightning destinations because the Lightning client selects its own coins.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Ark transaction ID for non-Lightning payments. For Lightning payments returns <c>null</c>.
    /// </returns>
    public async Task<string?> Spend(
        StoreData store,
        string destination,
        long? amountSats,
        IReadOnlyList<string>? inputOutpoints,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        destination = (destination ?? throw new ArgumentNullException(nameof(destination))).Trim();

        var config = GetConfig<ArkadePaymentMethodConfig>(ArkadePlugin.ArkadePaymentMethodId, store);

        if (config?.WalletId is null)
            throw new IncompleteArkadeSetupException("arkade wallet setup was not done!");

        if (!config.GeneratedByStore)
            throw new IncompleteArkadeSetupException("Wallet does not belong to the current store.");

        if (amountSats is < 0)
            throw new MalformedPaymentDestination("Amount must be non-negative.");

        var hasExplicitInputs = inputOutpoints is { Count: > 0 };

        var terms = await clientTransport.GetServerInfoAsync(cancellationToken);

        // Lightning destinations: BOLT11 invoice (optionally lightning: prefixed)
        if (destination.Replace("lightning:", "", StringComparison.InvariantCultureIgnoreCase) is { } lnbolt11 &&
            BOLT11PaymentRequest.TryParse(lnbolt11, out var bolt11, terms.Network))
        {
            if (bolt11 is null)
            {
                throw new MalformedPaymentDestination();
            }

            if (amountSats.HasValue)
                throw new MalformedPaymentDestination(
                    "amountSats is not supported for Lightning destinations: amount is determined by the BOLT11 invoice.");

            if (hasExplicitInputs)
                throw new MalformedPaymentDestination(
                    "inputOutpoints is not supported for Lightning destinations: the Lightning client manages coin selection.");

            var lnConfig =
                store
                    .GetPaymentMethodConfig<LightningPaymentMethodConfig>(
                        GetLightningPaymentMethod(),
                        paymentMethodHandlerDictionary
                    );

            if (lnConfig is null)
            {
                throw new IncompleteArkadeSetupException("lightning compatibility is not enabled");
            }

            var lnClient = paymentMethodHandlerDictionary.GetLightningHandler("BTC").CreateLightningClient(lnConfig);
            if (lnClient is not ArkLightningClient)
            {
                throw new IncompleteArkadeSetupException("lightning compatibility is not enabled");
            }

            var resp = await lnClient.Pay(bolt11.ToString(), cancellationToken);
            return resp.Result == PayResult.Ok ? null : throw new ArkadePaymentFailedException($"Payment failed: {resp?.ErrorDetail}");
        }

        // Resolve destination + amount for Ark-targeted payments.
        var (arkAddress, parsedAmount) = TryResolveArkDestination(destination);
        if (arkAddress is null)
            throw new MalformedPaymentDestination();

        // Amount precedence: explicit amountSats > amount encoded in destination.
        Money? amount = amountSats.HasValue ? Money.Satoshis(amountSats.Value) : parsedAmount;
        if (amount is null || amount == Money.Zero)
            throw new MalformedPaymentDestination(
                "Amount is required: provide amountSats, or include an amount in the BIP21 URI.");

        var output = new ArkTxOut(ArkTxOutType.Vtxo, amount, arkAddress);

        try
        {
            uint256 txId;
            if (hasExplicitInputs)
            {
                var selectedCoins = await ResolveCoinsForOutpoints(config.WalletId, inputOutpoints!, cancellationToken);
                txId = await arkadeSpender.Spend(config.WalletId, selectedCoins, [output], cancellationToken);
            }
            else
            {
                txId = await arkadeSpender.Spend(config.WalletId, [output], cancellationToken);
            }

            // Poll for VTXO updates on active contracts — constrain to the last few
            // minutes so wallets with large historical VTXO counts don't re-fetch everything.
            var activeContracts = await contractStorage.GetContracts(
                walletIds: [config.WalletId], isActive: true, cancellationToken: cancellationToken);
            await vtxoSyncService.PollScriptsForVtxos(
                activeContracts.Select(c => c.Script).ToHashSet(),
                after: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
                cancellationToken);

            return txId.ToString();
        }
        catch (MalformedPaymentDestination)
        {
            throw;
        }
        catch (Exception e) when (e is not ArkadePaymentFailedException && e is not OperationCanceledException)
        {
            throw new ArkadePaymentFailedException(e.Message);
        }
    }

    /// <summary>
    /// Try to parse <paramref name="destination"/> as a bare Ark address or a BIP21 URI carrying one,
    /// returning the resolved address and any embedded amount.
    /// </summary>
    private static (ArkAddress? Address, Money? Amount) TryResolveArkDestination(string destination)
    {
        // Bare Ark address (no URI scheme)
        if (ArkAddress.TryParse(destination, out var bareAddress) && bareAddress is not null)
        {
            return (bareAddress, null);
        }

        // BIP21 URI: bitcoin:<host>?ark=<addr>&amount=<btc>
        if (Uri.TryCreate(destination, UriKind.Absolute, out var uri) &&
            uri.Scheme.Equals("bitcoin", StringComparison.InvariantCultureIgnoreCase))
        {
            // uri.Host is empty for bitcoin: URIs, so we must parse the host portion ourselves.
            var host = uri.AbsoluteUri[(uri.Scheme.Length + 1)..].Split('?')[0];
            var qs = uri.ParseQueryString();

            ArkAddress? address = null;
            if (ArkAddress.TryParse(host, out var hostAddress) && hostAddress is not null)
            {
                address = hostAddress;
            }
            else if (qs["ark"] is { } arkQs && ArkAddress.TryParse(arkQs, out var qsAddress) && qsAddress is not null)
            {
                address = qsAddress;
            }

            if (address is null)
                return (null, null);

            Money? amount = null;
            if (qs["amount"] is { Length: > 0 } amountStr &&
                decimal.TryParse(amountStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var amountBtc) &&
                amountBtc > 0)
            {
                amount = Money.Coins(amountBtc);
            }

            return (address, amount);
        }

        return (null, null);
    }

    /// <summary>
    /// Resolve the provided <c>txid:vout</c> outpoint strings to <see cref="ArkCoin"/>s from the wallet's
    /// available coin set. Throws if any outpoint cannot be found or is malformed.
    /// </summary>
    private async Task<ArkCoin[]> ResolveCoinsForOutpoints(
        string walletId,
        IReadOnlyList<string> outpoints,
        CancellationToken cancellationToken)
    {
        var available = await arkadeSpender.GetAvailableCoins(walletId, cancellationToken);
        var byOutpoint = available.ToDictionary(c => $"{c.Outpoint.Hash}:{c.Outpoint.N}");
        var resolved = new List<ArkCoin>(outpoints.Count);
        var missing = new List<string>();

        foreach (var raw in outpoints)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var trimmed = raw.Trim();
            var parts = trimmed.Split(':');
            if (parts.Length != 2 || !uint256.TryParse(parts[0], out _) || !uint.TryParse(parts[1], out _))
            {
                throw new MalformedPaymentDestination(
                    $"Input outpoint '{trimmed}' is not a valid txid:vout string.");
            }

            if (byOutpoint.TryGetValue(trimmed, out var coin))
            {
                resolved.Add(coin);
            }
            else
            {
                missing.Add(trimmed);
            }
        }

        if (resolved.Count == 0)
            throw new ArkadePaymentFailedException(
                "None of the provided input outpoints match a spendable coin in this wallet.");

        if (missing.Count > 0)
            throw new ArkadePaymentFailedException(
                $"The following input outpoints are not spendable from this wallet: {string.Join(", ", missing)}.");

        return resolved.ToArray();
    }

    private static PaymentMethodId GetLightningPaymentMethod() => PaymentTypes.LN.GetPaymentMethodId("BTC");

    private T? GetConfig<T>(PaymentMethodId paymentMethodId, StoreData store) where T : class
    {
        return store.GetPaymentMethodConfig<T>(paymentMethodId, paymentMethodHandlerDictionary);
    }

}
