using Ark.V1;
using NArk.Models;
using NArk.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Extensions;

public static class ArkExtensions
{

    public static Sequence Parse(long val)
    {
        return val >= 512 ? new Sequence(TimeSpan.FromSeconds(val)) : new Sequence((int)val);
    }


    public static ArkOperatorTerms ArkOperatorTerms(this GetInfoResponse response)
    {
        var network = Network.GetNetwork(response.Network)?? (response.Network.Equals("bitcoin", StringComparison.InvariantCultureIgnoreCase)? Network.Main : null);
        
        if (network == null)
            throw new ArgumentException($"Unknown network {response.Network}");
        
        return new ArkOperatorTerms(
            Dust: Money.Satoshis(response.Dust),
            SignerKey: KeyExtensions.ParseOutputDescriptor(response.SignerPubkey, network),
            DeprecatedSigners: response.DeprecatedSigners.ToDictionary(signer => signer.Pubkey.ToECXOnlyPubKey(),
                signer => signer.CutoffDate),
            Network: network,
            UnilateralExit: Parse(response.UnilateralExitDelay),
            BoardingExit: Parse(response.BoardingExitDelay),
            ForfeitAddress: BitcoinAddress.Create(response.ForfeitAddress, network),
            ForfeitPubKey: response.ForfeitPubkey.ToECXOnlyPubKey(),
            CheckpointTapscript: new CheckpointTapscript(Script.FromHex(response.CheckpointTapscript)),
            FeeTerms: response.Fees.ArkOperatorFeeTerms()
        );
    }

    private static ArkOperatorFeeTerms ArkOperatorFeeTerms(this FeeInfo? feeInfo)
    {
        var defaults = new ArkOperatorFeeTerms(
            TxFeeRate: Money.Zero, 
            OffchainOutput: Money.Zero,
            OnchainOutput: Money.Zero,
            OffchainInput: Money.Zero,
            OnchainInput: Money.Zero
        );

        if (decimal.TryParse(feeInfo?.TxFeeRate, out var txFeeRate))
            defaults = defaults with { TxFeeRate = Money.Satoshis(txFeeRate) };
        if (decimal.TryParse(feeInfo?.IntentFee.OffchainOutput, out var offchainOutputFee))
            defaults = defaults with { OffchainOutput = Money.Satoshis(offchainOutputFee) };
        if (decimal.TryParse(feeInfo?.IntentFee.OffchainInput, out var offchainInput))
            defaults = defaults with { OffchainInput = Money.Satoshis(offchainInput) };
        if (decimal.TryParse(feeInfo?.IntentFee.OnchainOutput, out var onchainOutputFee))
            defaults = defaults with { OnchainOutput = Money.Satoshis(onchainOutputFee) };
        if (decimal.TryParse(feeInfo?.IntentFee.OnchainInput, out var onchainInput))
            defaults = defaults with { OnchainInput = Money.Satoshis(onchainInput) };

        return defaults;
    }

    class CheckpointTapscript( Script serverProvidedScript)
        : UnilateralPathArkTapScript(Sequence.Final, new NofNMultisigTapScript([]))
    {
        public override IEnumerable<Op> BuildScript()
        {
            return serverProvidedScript.ToOps();
        }
    }
}
