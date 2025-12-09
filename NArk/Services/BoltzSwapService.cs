using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using NArk.Boltz.Client;
using NArk.Boltz.Models.Swaps.Reverse;
using NArk.Boltz.Models.Swaps.Submarine;
using NArk.Contracts;
using NArk.Extensions;
using NArk.Models;
using NArk.Services.Abstractions;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace NArk.Services;

public class BoltzSwapService(
    BoltzClient boltzClient,
    IOperatorTermsService operatorTermsService,
    ILogger<BoltzSwapService> logger)
{
    public async Task<SubmarineSwapResult> CreateSubmarineSwap(BOLT11PaymentRequest invoice, ECPubKey sender,
        CancellationToken cancellationToken = default)
    {
        var operatorTerms = await operatorTermsService.GetOperatorTerms(cancellationToken);

        var response = await boltzClient.CreateSubmarineSwapAsync(new SubmarineRequest()
        {
            Invoice = invoice.ToString(),
            RefundPublicKey = sender.ToHex(),
            From = "ARK",
            To = "BTC",
        }, cancellationToken);

        var hash = new uint160(Hashes.RIPEMD160(invoice.PaymentHash.ToBytes(false)), false);
        var receiver = response.ClaimPublicKey.ToECXOnlyPubKey();

        var vhtlcContract = new VHTLCContract(
            server: operatorTerms.SignerKey,
            sender: sender.ToXOnlyPubKey(),
            receiver: receiver,
            hash: hash,
            refundLocktime: new LockTime(response.TimeoutBlockHeights.Refund),
            unilateralClaimDelay: ArkExtensions.Parse( response.TimeoutBlockHeights.UnilateralClaim),
            unilateralRefundDelay: ArkExtensions.Parse(response.TimeoutBlockHeights.UnilateralRefund),
            unilateralRefundWithoutReceiverDelay: ArkExtensions.Parse(response.TimeoutBlockHeights
                .UnilateralRefundWithoutReceiver)
        );


        var address = vhtlcContract.GetArkAddress();
        if (response.Address != address.ToString(operatorTerms.Network.ChainName == ChainName.Mainnet))
            throw new Exception($"Address mismatch! Expected {address.ToString(operatorTerms.Network.ChainName == ChainName.Mainnet)} got {response.Address}");

        return new SubmarineSwapResult(vhtlcContract, response, address);
    }

    public async Task<ReverseSwapResult> CreateReverseSwap(CreateInvoiceParams createInvoiceRequest,
        ECPubKey receiver,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Creating reverse swap with invoice amount {InvoiceAmount} for receiver {Receiver}",
            createInvoiceRequest.Amount.ToUnit(LightMoneyUnit.BTC), receiver.ToHex());

        // Get operator terms 
        var operatorTerms = await operatorTermsService.GetOperatorTerms(cancellationToken);

        // Generate preimage and compute preimage hash using SHA256 for Boltz
        var preimage = RandomUtils.GetBytes(32);
        var preimageHash = Hashes.SHA256(preimage);
        logger.LogInformation("Generated preimage hash: {PreimageHash}", Encoders.Hex.EncodeData(preimageHash));

        // First make the Boltz request to get the swap details including timeout block heights
        // Use OnchainAmount so the merchant receives the full requested amount (user pays swap fees)
        var request = new ReverseRequest
        {
            From = "BTC",
            To = "ARK",
            OnchainAmount = createInvoiceRequest.Amount.MilliSatoshi/1000,
            ClaimPublicKey = receiver.ToHex(), // Receiver will claim the VTXO
            PreimageHash = Encoders.Hex.EncodeData(preimageHash),
            AcceptZeroConf = true,
            DescriptionHash = createInvoiceRequest.DescriptionHash?.ToString(),
            Description = createInvoiceRequest.Description,
            InvoiceExpirySeconds = Convert.ToInt32(createInvoiceRequest.Expiry.TotalSeconds),
        };

        logger.LogDebug("Sending reverse swap request to Boltz");
        var response = await boltzClient.CreateReverseSwapAsync(request, cancellationToken);

        if (response == null)
        {
            logger.LogError("Failed to create reverse swap - null response from Boltz");
            throw new InvalidOperationException("Failed to create reverse swap");
        }

        logger.LogInformation("Received reverse swap response from Boltz with ID: {SwapId}", response.Id);

        // Extract the sender key from Boltz's response (refundPublicKey)
        if (string.IsNullOrEmpty(response.RefundPublicKey))
        {
            logger.LogError("Boltz did not provide refund public key");
            throw new InvalidOperationException("Boltz did not provide refund public key");
        }
        
        var bolt11 = BOLT11PaymentRequest.Parse(response.Invoice, operatorTerms.Network);
        if (!bolt11.PaymentHash.ToBytes(false).SequenceEqual(preimageHash))
        {
            throw new InvalidOperationException("Boltz did not provide the correct preimage hash");
        }
        
        // Verify the invoice amount is greater than onchain amount (includes fees)
        var invoiceAmountSats = bolt11.MinimumAmount.ToUnit(LightMoneyUnit.Satoshi);
        var onchainAmountSats = createInvoiceRequest.Amount.ToUnit(LightMoneyUnit.Satoshi);
        if (invoiceAmountSats <= onchainAmountSats)
        {
            throw new InvalidOperationException($"Invoice amount ({invoiceAmountSats} sats) must be greater than onchain amount ({onchainAmountSats} sats) to cover swap fees");
        }
        
        var swapFee = invoiceAmountSats - onchainAmountSats;
        logger.LogInformation("Reverse swap created: onchain amount = {OnchainAmount} sats, invoice amount = {InvoiceAmount} sats, swap fee = {SwapFee} sats",
            onchainAmountSats, invoiceAmountSats, swapFee);
        
        
        var sender = response.RefundPublicKey.ToECXOnlyPubKey();
        logger.LogDebug("Using sender key: {SenderKey}", response.RefundPublicKey);

        var vhtlcContract = new VHTLCContract(
            server: operatorTerms.SignerKey,
            sender: sender,
            receiver: receiver.ToXOnlyPubKey(),
            preimage: preimage,
            refundLocktime: new LockTime(response.TimeoutBlockHeights.Refund),
            unilateralClaimDelay: ArkExtensions.Parse(response.TimeoutBlockHeights.UnilateralClaim),
            unilateralRefundDelay:ArkExtensions.Parse( response.TimeoutBlockHeights.UnilateralRefund),
            unilateralRefundWithoutReceiverDelay: ArkExtensions.Parse( response.TimeoutBlockHeights
                .UnilateralRefundWithoutReceiver)
        );

        // Get the claim address and validate it matches Boltz's lockup address
        var arkAddress = vhtlcContract.GetArkAddress();
        var claimAddress = arkAddress.ToString(mainnet: operatorTerms.Network == Network.Main);
        logger.LogDebug("Generated claim address: {ClaimAddress}", claimAddress);

        // Validate that our computed address matches what Boltz expects
        if (claimAddress != response.LockupAddress)
        {
            logger.LogWarning("Address mismatch: computed {ComputedAddress}, Boltz expects {BoltzAddress}",
                claimAddress, response.LockupAddress);
            throw new InvalidOperationException(
                $"Address mismatch: computed {claimAddress}, Boltz expects {response.LockupAddress}");
        }

        logger.LogInformation("Successfully created reverse swap with ID: {SwapId}, lockup address: {LockupAddress}, pr: {Pr}",
            response.Id, response.LockupAddress, response.Invoice);

        return new ReverseSwapResult(vhtlcContract, response, preimageHash);
    }
}