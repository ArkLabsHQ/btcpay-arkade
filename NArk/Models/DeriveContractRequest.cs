using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Models;

public record DeriveContractRequest(ArkOperatorTerms OperatorTerms, OutputDescriptor User, byte[]? Tweak = null);