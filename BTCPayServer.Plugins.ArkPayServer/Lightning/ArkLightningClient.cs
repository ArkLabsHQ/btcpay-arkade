using System.ComponentModel.DataAnnotations;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using BTCPayServer.Plugins.ArkPayServer.Storage;
using Microsoft.Extensions.Logging;
using NArk.Contracts;
using NArk.Swaps.Services;
using NArk.Transport;
using NBitcoin;
using NodeInfo = BTCPayServer.Lightning.NodeInfo;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

public class ArkLightningClient(
    IClientTransport clientTransport,
    Network network,
    string walletId,
    SwapsManagementService swapsManagementService,
    BoltzLimitsService boltzLimitsService,
    EfCoreSwapStorage swapStorage,
    EfCoreContractStorage contractStorage,
    EfCoreVtxoStorage vtxoStorage,
    ILogger<ArkLightningInvoiceListener> logger) : IExtendedLightningClient
{
    public async Task<LightningInvoice?> GetInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        var reverseSwap = await swapStorage.GetSwapWithContractAsync(walletId, invoiceId, cancellation);

        if (reverseSwap == null || reverseSwap.SwapType != ArkSwapType.ReverseSubmarine)
            return null;

        return Map(reverseSwap, network);
    }

    public async Task<LightningInvoice?> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default)
    {
        var paymentHashStr = paymentHash.ToString();
        var reverseSwap = await swapStorage.GetSwapByHashWithContractAsync(
            walletId, paymentHashStr, ArkSwapType.ReverseSubmarine, cancellation);

        return reverseSwap == null ? null : Map(reverseSwap, network);
    }

    public static LightningInvoice Map(ArkSwap reverseSwap, Network network)
    {
        var bolt11 = BOLT11PaymentRequest.Parse(reverseSwap.Invoice, network);

        var lightningStatus = reverseSwap.Status switch
        {
            ArkSwapStatus.Settled => LightningInvoiceStatus.Paid,
            ArkSwapStatus.Failed => LightningInvoiceStatus.Expired,
            ArkSwapStatus.Pending => LightningInvoiceStatus.Unpaid,
            _ => throw new NotSupportedException()
        };

        var contract =
            ArkContractParser.Parse(reverseSwap.Contract.Type, reverseSwap.Contract.ContractData, network) as VHTLCContract;

        return new LightningInvoice
        {
            Id = reverseSwap.SwapId,
            Amount = bolt11.MinimumAmount,
            Status = lightningStatus,
            ExpiresAt = bolt11.ExpiryDate,
            BOLT11 = reverseSwap.Invoice,
            PaymentHash = bolt11.PaymentHash?.ToString(),
            PaidAt = lightningStatus == LightningInvoiceStatus.Paid ? reverseSwap.UpdatedAt : null,
            // we have to comment this out because BTCPay will consider this invoice as partially paid..
            // AmountReceived = lightningStatus == LightningInvoiceStatus.Paid
            //     ? LightMoney.Satoshis(reverseSwap.ExpectedAmount)
            //     : null,
            Preimage = contract?.Preimage != null ? Convert.ToHexString(contract.Preimage).ToLowerInvariant() : null,
        };
    }

    public async Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = default)
    {
        return await ListInvoices(new ListInvoicesParams(), cancellation);
    }

    public async Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request,
        CancellationToken cancellation = default)
    {
        var reverseSwaps = await swapStorage.ListReverseSwapsWithContractAsync(
            walletId, request.PendingOnly, (int)request.OffsetIndex.GetValueOrDefault(0), cancellation);

        var invoices = new List<LightningInvoice>();
        foreach (var swap in reverseSwaps)
        {
            try
            {
                invoices.Add(Map(swap, network));
            }
            catch
            {
                // Skip failed invoices
            }
        }

        return invoices.ToArray();
    }

    public async Task<LightningPayment> GetPayment(string paymentHash, CancellationToken cancellation = default)
    {
        var swap = await swapStorage.GetSwapByHashWithContractAsync(
            walletId, paymentHash, ArkSwapType.Submarine, cancellation);

        if (swap == null)
            throw new KeyNotFoundException("Swap with the given payment hash was not found");

        return MapPayment(swap);
    }

    private LightningPayment MapPayment(ArkSwap swap)
    {
        var bolt11 = BOLT11PaymentRequest.Parse(swap.Invoice, network);
        var status = swap.Status switch
        {
            ArkSwapStatus.Settled => LightningPaymentStatus.Complete,
            ArkSwapStatus.Failed => LightningPaymentStatus.Failed,
            ArkSwapStatus.Pending => LightningPaymentStatus.Pending,
            _ => LightningPaymentStatus.Unknown
        };
        var htlcContract = ArkContractParser.Parse(swap.Contract.Type, swap.Contract.ContractData, network) as VHTLCContract;

        return new LightningPayment
        {
            Id = swap.SwapId,
            Amount = bolt11.MinimumAmount,
            Status = status,
            BOLT11 = swap.Invoice,
            PaymentHash = bolt11.PaymentHash?.ToString(),
            Preimage = htlcContract?.Preimage != null ? Convert.ToHexString(htlcContract.Preimage).ToLowerInvariant() : null,
            CreatedAt = swap.CreatedAt,
            AmountSent = LightMoney.Satoshis(swap.ExpectedAmount),
        };
    }

    public Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default)
    {
        return ListPayments(new ListPaymentsParams(), cancellation);
    }

    public async Task<LightningPayment[]> ListPayments(ListPaymentsParams request,
        CancellationToken cancellation = default)
    {
        var swaps = await swapStorage.ListSubmarineSwapsWithContractAsync(
            walletId, (int)request.OffsetIndex.GetValueOrDefault(0), cancellation);

        return [.. swaps.Select(MapPayment)];
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
        CancellationToken cancellation = default)
    {
        var createInvoiceParams = new CreateInvoiceParams(amount, description, expiry);
        return await CreateInvoice(createInvoiceParams, cancellation);
    }

    public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest,
        CancellationToken cancellation = default)
    {
        var terms = await clientTransport.GetServerInfoAsync(cancellation);
        if (terms.Dust > createInvoiceRequest.Amount)
        {
            throw new InvalidOperationException("Sub-dust amounts are not supported");
        }

        // Validate amount against Boltz limits
        var amountSats = (long)createInvoiceRequest.Amount.ToUnit(LightMoneyUnit.Satoshi);
        var (isValid, errorMessage) = await boltzLimitsService.ValidateAmountAsync(amountSats, isReverse: true, cancellation);
        if (!isValid)
        {
            throw new InvalidOperationException(errorMessage);
        }

        // Create reverse swap via NNark's SwapsManagementService
        var invoice = await swapsManagementService.InitiateReverseSwap(walletId, createInvoiceRequest, cancellation);

        // Fetch the created swap from DB to return proper LightningInvoice
        var swap = await swapStorage.GetSwapByInvoiceWithContractAsync(walletId, invoice, cancellation);

        if (swap == null)
        {
            throw new InvalidOperationException("Failed to create reverse swap");
        }

        return Map(swap, network);
    }

    public Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default)
    {
        return Task.FromResult<ILightningInvoiceListener>(
            new ArkLightningInvoiceListener(walletId, logger, swapStorage, network, cancellation));
    }

    public Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
    }

    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = default)
    {
        var contractScripts = await contractStorage.GetContractScriptsAsync(walletId, cancellation);
        var sum = await vtxoStorage.SumUnspentBalanceByContractScriptsAsync(contractScripts, cancellation);

        return new LightningNodeBalance()
        {
            OffchainBalance = new OffchainBalance()
            {
                Local = LightMoney.Satoshis(sum)
            }
        };
    }

    public Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = default)
    {
        throw new NotSupportedException("BOLT11 is required");
    }

    public async Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = default)
    {
        try
        {
            if (string.IsNullOrEmpty(bolt11))
            {
                throw new NotSupportedException("BOLT11 is required");
            }

            var pr = BOLT11PaymentRequest.Parse(bolt11, network);

            // Validate amount against Boltz limits
            var amountSats = (long)(pr.MinimumAmount?.ToUnit(LightMoneyUnit.Satoshi) ?? 0);
            var (isValid, errorMessage) = await boltzLimitsService.ValidateAmountAsync(amountSats, isReverse: false, cancellation);
            if (!isValid)
            {
                return new PayResponse(PayResult.Error, errorMessage);
            }

            // Create submarine swap via NNark's SwapsManagementService
            await swapsManagementService.InitiateSubmarineSwap(walletId, pr, autoPay: true, cancellation);

            // Fetch the created swap from DB to return proper PayResponse
            var result = await swapStorage.GetSwapByInvoiceWithContractAsync(walletId, bolt11, cancellation);

            if (result == null)
            {
                return new PayResponse(PayResult.Error, "Failed to create submarine swap");
            }

            var payment = MapPayment(result);
            return new PayResponse()
            {
                Details = new PayDetails()
                {
                    PaymentHash = pr.PaymentHash,
                    Preimage = string.IsNullOrEmpty(payment.Preimage) ? null : new uint256(payment.Preimage),
                    Status = payment.Status,
                    FeeAmount = payment.Fee,
                    TotalAmount = payment.AmountSent
                }
            };
        }
        catch (Exception e)
        {
            return new PayResponse(PayResult.Error, e.Message);
        }
    }

    public Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default)
    {
        return Pay(bolt11, new PayInvoiceParams(), cancellation);
    }

    public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest,
        CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
    }

    public Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
    }

    public Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
    }

    public Task CancelInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
    }

    public Task<LightningChannel[]> ListChannels(CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
    }

    public Task<ValidationResult?> Validate()
    {
        return Task.FromResult(ValidationResult.Success);
    }

    public string DisplayName => "Arkade Lightning (Boltz)";
    public Uri? ServerUri => null;

    public override string ToString() => $"type=arkade;wallet-id={walletId}";
}