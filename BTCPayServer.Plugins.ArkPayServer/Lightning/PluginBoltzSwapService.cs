using BTCPayServer.Lightning;
using NArk.Contracts;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Boltz.Models.Swaps.Reverse;
using NArk.Swaps.Boltz.Models.Swaps.Submarine;
using NArk.Transport;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace BTCPayServer.Plugins.ArkPayServer.Lightning;

/// <summary>
/// Plugin-local wrapper for Boltz swap operations.
/// Required because NNark's BoltzSwapService is internal.
/// </summary>
public class PluginBoltzSwapService(BoltzClient boltzClient, IClientTransport clientTransport)
{
    private static Sequence ParseSequence(long val)
    {
        return val >= 512 ? new Sequence(TimeSpan.FromSeconds(val)) : new Sequence((int)val);
    }

    /// <summary>
    /// Converts an ECPubKey to an OutputDescriptor in tr(pubkey) format.
    /// </summary>
    private static OutputDescriptor ToOutputDescriptor(ECPubKey pubKey, Network network)
    {
        var hexPubKey = Convert.ToHexString(pubKey.ToBytes()).ToLowerInvariant();
        return OutputDescriptor.Parse($"tr({hexPubKey})", network);
    }

    /// <summary>
    /// Parses a hex public key string into an OutputDescriptor.
    /// </summary>
    private static OutputDescriptor ParseOutputDescriptor(string str, Network network)
    {
        if (!HexEncoder.IsWellFormed(str))
            return OutputDescriptor.Parse(str, network);

        var bytes = Convert.FromHexString(str);
        if (bytes.Length != 32 && bytes.Length != 33)
        {
            throw new ArgumentException("the string must be 32/33 bytes long", nameof(str));
        }

        return OutputDescriptor.Parse($"tr({str})", network);
    }

    public async Task<SubmarineSwapResult> CreateSubmarineSwap(BOLT11PaymentRequest invoice, ECPubKey sender,
        CancellationToken cancellationToken = default)
    {
        var operatorTerms = await clientTransport.GetServerInfoAsync(cancellationToken);
        var senderDescriptor = ToOutputDescriptor(sender, operatorTerms.Network);

        var response = await boltzClient.CreateSubmarineSwapAsync(new SubmarineRequest()
        {
            Invoice = invoice.ToString(),
            RefundPublicKey = Convert.ToHexString(sender.ToBytes()).ToLowerInvariant(),
            From = "ARK",
            To = "BTC",
        }, cancellationToken);

        if (invoice.PaymentHash is null)
            throw new InvalidOperationException("Invoice does not contain valid payment hash");

        var hash = new uint160(Hashes.RIPEMD160(invoice.PaymentHash.ToBytes(false)), false);

        var vhtlcContract = new VHTLCContract(
            server: operatorTerms.SignerKey,
            sender: senderDescriptor,
            receiver: ParseOutputDescriptor(response.ClaimPublicKey, operatorTerms.Network),
            hash: hash,
            refundLocktime: new LockTime(response.TimeoutBlockHeights.Refund),
            unilateralClaimDelay: ParseSequence(response.TimeoutBlockHeights.UnilateralClaim),
            unilateralRefundDelay: ParseSequence(response.TimeoutBlockHeights.UnilateralRefund),
            unilateralRefundWithoutReceiverDelay: ParseSequence(response.TimeoutBlockHeights
                .UnilateralRefundWithoutReceiver)
        );


        var address = vhtlcContract.GetArkAddress();
        if (response.Address != address.ToString(operatorTerms.Network.ChainName == ChainName.Mainnet))
            throw new Exception(
                $"Address mismatch! Expected {address.ToString(operatorTerms.Network.ChainName == ChainName.Mainnet)} got {response.Address}");

        return new SubmarineSwapResult(vhtlcContract, response, address);
    }

    public async Task<ReverseSwapResult> CreateReverseSwap(CreateInvoiceParams createInvoiceRequest,
        ECPubKey receiver,
        CancellationToken cancellationToken = default)
    {
        // Get operator terms
        var operatorTerms = await clientTransport.GetServerInfoAsync(cancellationToken);
        var receiverDescriptor = ToOutputDescriptor(receiver, operatorTerms.Network);

        // Generate preimage and compute preimage hash using SHA256 for Boltz
        var preimage = RandomUtils.GetBytes(32);
        var preimageHash = Hashes.SHA256(preimage);

        // First make the Boltz request to get the swap details including timeout block heights
        // Use OnchainAmount so the merchant receives the full requested amount (user pays swap fees)
        var request = new ReverseRequest
        {
            From = "BTC",
            To = "ARK",
            OnchainAmount = (long)createInvoiceRequest.Amount.ToUnit(LightMoneyUnit.Satoshi),
            ClaimPublicKey = Convert.ToHexString(receiver.ToBytes()).ToLowerInvariant(),
            PreimageHash = Encoders.Hex.EncodeData(preimageHash),
            AcceptZeroConf = true,
            DescriptionHash = createInvoiceRequest.DescriptionHash?.ToString(),
            Description = createInvoiceRequest.Description,
            InvoiceExpirySeconds = Convert.ToInt32(createInvoiceRequest.Expiry.TotalSeconds),
        };

        var response = await boltzClient.CreateReverseSwapAsync(request, cancellationToken);

        if (response == null)
        {
            throw new InvalidOperationException("Failed to create reverse swap, null response from Boltz");
        }

        // Extract the sender key from Boltz's response (refundPublicKey)
        if (string.IsNullOrEmpty(response.RefundPublicKey))
        {
            throw new InvalidOperationException("Boltz did not provide refund public key");
        }

        var bolt11 = BOLT11PaymentRequest.Parse(response.Invoice, operatorTerms.Network);
        if (bolt11.PaymentHash is null || !bolt11.PaymentHash.ToBytes(false).SequenceEqual(preimageHash))
        {
            throw new InvalidOperationException("Boltz did not provide the correct preimage hash");
        }

        // Verify the invoice amount is greater than onchain amount (includes fees)
        var invoiceAmountSats = bolt11.MinimumAmount.ToUnit(LightMoneyUnit.Satoshi);
        var onchainAmountSats = createInvoiceRequest.Amount.ToUnit(LightMoneyUnit.Satoshi);
        if (invoiceAmountSats < onchainAmountSats)
        {
            throw new InvalidOperationException(
                $"Invoice amount ({invoiceAmountSats} sats) must be greater than onchain amount ({onchainAmountSats} sats) to cover swap fees");
        }

        var vhtlcContract = new VHTLCContract(
            server: operatorTerms.SignerKey,
            sender: ParseOutputDescriptor(response.RefundPublicKey, operatorTerms.Network),
            receiver: receiverDescriptor,
            preimage: preimage,
            refundLocktime: new LockTime(response.TimeoutBlockHeights.Refund),
            unilateralClaimDelay: ParseSequence(response.TimeoutBlockHeights.UnilateralClaim),
            unilateralRefundDelay: ParseSequence(response.TimeoutBlockHeights.UnilateralRefund),
            unilateralRefundWithoutReceiverDelay: ParseSequence(response.TimeoutBlockHeights
                .UnilateralRefundWithoutReceiver)
        );

        // Get the claim address and validate it matches Boltz's lockup address
        var arkAddress = vhtlcContract.GetArkAddress();
        var claimAddress = arkAddress.ToString(isMainnet: operatorTerms.Network == Network.Main);

        // Validate that our computed address matches what Boltz expects
        if (claimAddress != response.LockupAddress)
        {
            throw new InvalidOperationException(
                $"Address mismatch: computed {claimAddress}, Boltz expects {response.LockupAddress}");
        }


        return new ReverseSwapResult(vhtlcContract, response, preimageHash);
    }
}
