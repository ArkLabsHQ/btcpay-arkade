using System.ComponentModel.DataAnnotations;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Core.Contracts;
using NArk.Core.Services;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz;
using NArk.Swaps.Models;
using NArk.Swaps.Services;
using NArk.Core.Transport;
using NBitcoin;
using NodeInfo = BTCPayServer.Lightning.NodeInfo;

namespace BTCPayServer.Plugins.Arkade.Lightning;

public class ArkLightningClient(
    IClientTransport clientTransport,
    Network network,
    string walletId,
    SwapsManagementService swapsManagementService,
    BoltzLimitsValidator boltzLimitsValidator,
    ISwapStorage swapStorage,
    IContractStorage contractStorage,
    ISpendingService spendingService,
    IChainTimeProvider chainTimeProvider,
    ILogger<ArkLightningInvoiceListener> logger) : IExtendedLightningClient
{
    /// <summary>
    /// Gets swaps with their contracts by fetching swaps first, then contracts separately.
    /// </summary>
    private async Task<IReadOnlyCollection<(ArkSwap Swap, ArkContractEntity? Contract)>> GetSwapsWithContractsAsync(
        string[]? swapIds = null,
        ArkSwapType? swapType = null,
        ArkSwapStatus? status = null,
        string? hash = null,
        string? invoice = null,
        int? skip = null,
        CancellationToken cancellation = default)
    {
        var swaps = await swapStorage.GetSwaps(
            walletId: walletId,
            swapIds: swapIds,
            swapType: swapType,
            status: status,
            hash: hash,
            invoice: invoice,
            skip: skip,
            cancellationToken: cancellation);

        if (swaps.Count == 0)
            return [];

        var contractScripts = swaps.Select(s => s.ContractScript).Distinct().ToArray();
        var contracts = await contractStorage.GetContracts(
            walletIds: [walletId],
            scripts: contractScripts,
            cancellationToken: cancellation);
        var contractDict = contracts.ToDictionary(c => c.Script);

        return swaps.Select(s => (s, contractDict.GetValueOrDefault(s.ContractScript))).ToList();
    }

    public async Task<LightningInvoice?> GetInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        var swapsWithContracts = await GetSwapsWithContractsAsync(
            swapIds: [invoiceId],
            cancellation: cancellation);
        var reverseSwap = swapsWithContracts.FirstOrDefault();

        if (reverseSwap.Swap == null || reverseSwap.Swap.SwapType != ArkSwapType.ReverseSubmarine)
            return null;

        return Map(reverseSwap.Swap, reverseSwap.Contract, network);
    }

    public async Task<LightningInvoice?> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default)
    {
        var paymentHashStr = paymentHash.ToString();
        var swapsWithContracts = await GetSwapsWithContractsAsync(
            hash: paymentHashStr,
            swapType: ArkSwapType.ReverseSubmarine,
            cancellation: cancellation);
        var reverseSwap = swapsWithContracts.FirstOrDefault();

        return reverseSwap.Swap == null ? null : Map(reverseSwap.Swap, reverseSwap.Contract, network);
    }

    public static LightningInvoice Map(ArkSwap swap, ArkContractEntity? contractEntity, Network network)
    {
        var bolt11 = BOLT11PaymentRequest.Parse(swap.Invoice, network);

        var lightningStatus = swap.Status switch
        {
            ArkSwapStatus.Settled => LightningInvoiceStatus.Paid,
            ArkSwapStatus.Failed => LightningInvoiceStatus.Expired,
            ArkSwapStatus.Pending => LightningInvoiceStatus.Unpaid,
            _ => throw new NotSupportedException()
        };

        VHTLCContract? contract = null;
        if (contractEntity != null)
        {
            contract = ArkContractParser.Parse(
                contractEntity.Type,
                contractEntity.AdditionalData,
                network) as VHTLCContract;
        }

        return new LightningInvoice
        {
            Id = swap.SwapId,
            Amount = bolt11.MinimumAmount,
            Status = lightningStatus,
            ExpiresAt = bolt11.ExpiryDate,
            BOLT11 = swap.Invoice,
            PaymentHash = bolt11.PaymentHash?.ToString(),
            PaidAt = lightningStatus == LightningInvoiceStatus.Paid ? swap.UpdatedAt.ToUniversalTime() : null,
            // we have to comment this out because BTCPay will consider this invoice as partially paid..
            // AmountReceived = lightningStatus == LightningInvoiceStatus.Paid
            //     ? LightMoney.Satoshis(swap.ExpectedAmount)
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
        var status = request.PendingOnly == true ? ArkSwapStatus.Pending : (ArkSwapStatus?)null;
        var reverseSwapsWithContracts = await GetSwapsWithContractsAsync(
            swapType: ArkSwapType.ReverseSubmarine,
            status: status,
            skip: (int)request.OffsetIndex.GetValueOrDefault(0),
            cancellation: cancellation);

        var invoices = new List<LightningInvoice>();
        foreach (var (swap, contract) in reverseSwapsWithContracts)
        {
            try
            {
                invoices.Add(Map(swap, contract, network));
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
        var swapsWithContracts = await GetSwapsWithContractsAsync(
            hash: paymentHash,
            swapType: ArkSwapType.Submarine,
            cancellation: cancellation);
        var result = swapsWithContracts.FirstOrDefault();

        if (result.Swap == null)
            throw new KeyNotFoundException("Swap with the given payment hash was not found");

        return MapPayment(result.Swap, result.Contract);
    }

    private LightningPayment MapPayment(ArkSwap swap, ArkContractEntity? contractEntity)
    {
        var bolt11 = BOLT11PaymentRequest.Parse(swap.Invoice, network);
        var status = swap.Status switch
        {
            ArkSwapStatus.Settled => LightningPaymentStatus.Complete,
            ArkSwapStatus.Failed => LightningPaymentStatus.Failed,
            ArkSwapStatus.Pending => LightningPaymentStatus.Pending,
            _ => LightningPaymentStatus.Unknown
        };

        VHTLCContract? htlcContract = null;
        if (contractEntity != null)
        {
            htlcContract = ArkContractParser.Parse(
                contractEntity.Type,
                contractEntity.AdditionalData,
                network) as VHTLCContract;
        }

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
        var swapsWithContracts = await GetSwapsWithContractsAsync(
            swapType: ArkSwapType.Submarine,
            skip: (int)request.OffsetIndex.GetValueOrDefault(0),
            cancellation: cancellation);

        return [.. swapsWithContracts.Select(s => MapPayment(s.Swap, s.Contract))];
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
        var (isValid, errorMessage) = await boltzLimitsValidator.ValidateAmountAsync(amountSats, isReverse: true, cancellation);
        if (!isValid)
        {
            throw new InvalidOperationException(errorMessage);
        }

        // Create reverse swap via NNark's SwapsManagementService
        var invoice = await swapsManagementService.InitiateReverseSwap(walletId, createInvoiceRequest, cancellation);

        // Fetch the created swap from DB to return proper LightningInvoice
        var swapsWithContracts = await GetSwapsWithContractsAsync(
            invoice: invoice,
            cancellation: cancellation);
        var result = swapsWithContracts.FirstOrDefault();

        if (result.Swap == null)
        {
            throw new InvalidOperationException("Failed to create reverse swap");
        }

        return Map(result.Swap, result.Contract, network);
    }

    public Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default)
    {
        return Task.FromResult<ILightningInvoiceListener>(
            new ArkLightningInvoiceListener(walletId, logger, swapStorage, contractStorage, network, Map, cancellation));
    }

    public Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default)
    {
        throw new NotSupportedException();
    }

    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = default)
    {
        var availableCoins = await spendingService.GetAvailableCoins(walletId, cancellation);
        var chainTime = await chainTimeProvider.GetChainTime(cancellation);

        // Filter to only coins that can be spent offchain (not swept, not expired)
        var spendableCoins = availableCoins.Where(c => c.CanSpendOffchain(chainTime));
        var sum = spendableCoins.Sum(c => c.TxOut.Value.Satoshi);

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
            var (isValid, errorMessage) = await boltzLimitsValidator.ValidateAmountAsync(amountSats, isReverse: false, cancellation);
            if (!isValid)
            {
                return new PayResponse(PayResult.Error, errorMessage);
            }

            // Create submarine swap via NNark's SwapsManagementService
            await swapsManagementService.InitiateSubmarineSwap(walletId, pr, autoPay: true, cancellation);

            // Fetch the created swap from DB to return proper PayResponse
            var swapsWithContracts = await GetSwapsWithContractsAsync(
                invoice: bolt11,
                cancellation: cancellation);
            var result = swapsWithContracts.FirstOrDefault();

            if (result.Swap == null)
            {
                return new PayResponse(PayResult.Error, "Failed to create submarine swap");
            }

            var payment = MapPayment(result.Swap, result.Contract);
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