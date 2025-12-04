using NArk.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NArk.Models;

public record ArkOperatorTerms(
    Money Dust,
    ECXOnlyPubKey SignerKey,
    Dictionary<ECXOnlyPubKey, long> DeprecatedSigners,
    Network Network,
    Sequence UnilateralExit,
    Sequence BoardingExit,
    BitcoinAddress ForfeitAddress,
    ECXOnlyPubKey ForfeitPubKey,
    UnilateralPathArkTapScript CheckpointTapscript,
    ArkOperatorFeeTerms FeeTerms
);

public record ArkOperatorFeeTerms(
    Money TxFeeRate,
    Money OffchainOutput,
    Money OnchainOutput,
    Money OffchainInput,
    Money OnchainInput
);