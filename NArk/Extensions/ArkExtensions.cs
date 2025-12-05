using Ark.V1;
using NArk.Models;
using NArk.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Extensions;

public static class ArkExtensions
{
    public static ECXOnlyPubKey ServerKey(this GetInfoResponse response)
    {
        return response.SignerPubkey.ToECXOnlyPubKey();
    }

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
            SignerKey: response.ServerKey(),
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

    public static ArkOperatorFeeTerms ArkOperatorFeeTerms(this FeeInfo? feeInfo)
    {
        return new ArkOperatorFeeTerms(
            TxFeeRate: Money.Satoshis(decimal.Parse(feeInfo?.TxFeeRate ?? "0")), 
            OffchainOutput: Money.Satoshis(decimal.Parse(feeInfo?.IntentFee.OffchainOutput ?? "0")),
            OnchainOutput: Money.Satoshis(decimal.Parse(feeInfo?.IntentFee.OnchainOutput ?? "0")),
            OffchainInput: Money.Satoshis(decimal.Parse(feeInfo?.IntentFee.OffchainInput ?? "0")),
            OnchainInput: Money.Satoshis(decimal.Parse(feeInfo?.IntentFee.OnchainInput ?? "0"))
        );
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
