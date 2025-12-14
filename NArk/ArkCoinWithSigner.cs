using NArk.Contracts;
using NArk.Extensions;
using NArk.Scripts;
using NArk.Services;
using NArk.Services.Abstractions;
using NBitcoin;
using NBitcoin.Scripting;

namespace NArk;

public class SpendableArkCoin : ArkCoin
{
    public LockTime? SpendingLockTime { get; }
    public Sequence? SpendingSequence { get; }
    public ScriptBuilder SpendingScriptBuilder { get; set; }
    public TapScript SpendingScript => SpendingScriptBuilder.Build();
    public WitScript? SpendingConditionWitness { get; set; }

    public bool Recoverable { get; set; }

    public SpendableArkCoin(ArkContract contract,
        DateTimeOffset? expiresAt,
        uint? expiresAtHeight,
        OutPoint outpoint,
        TxOut txout,
        ScriptBuilder spendingScriptBuilder,
        WitScript? spendingConditionWitness,
        LockTime? lockTime,
        Sequence? sequence, bool recoverable) : base(contract, outpoint, txout, expiresAt, expiresAtHeight)
    {
        SpendingScriptBuilder = spendingScriptBuilder;
        SpendingConditionWitness = spendingConditionWitness;
        SpendingLockTime = lockTime;
        SpendingSequence = sequence;
        Recoverable = recoverable;


        if (sequence is null && spendingScriptBuilder.BuildScript().Contains(OpcodeType.OP_CHECKSEQUENCEVERIFY))
        {
            throw new InvalidOperationException("Sequence is required");
        }
    }
    
    public PSBTInput? FillPSBTInput(PSBT psbt)
    {
        var psbtInput = psbt.Inputs.FindIndexedInput(Outpoint);
        if (psbtInput is null)
        {
            return null;
        }

        psbtInput.SetArkFieldTapTree(Contract.GetTapScriptList());
        psbtInput.SetTaprootLeafScript(Contract.GetTaprootSpendInfo(), SpendingScript);
        if (SpendingConditionWitness is not null)
        {
            psbtInput.SetArkFieldConditionWitness(SpendingConditionWitness);
        }

        return psbtInput;
    }
}

public class SpendableArkCoinWithSigner : SpendableArkCoin
{
    public OutputDescriptor SignerDescriptor { get; }
    public IArkadeWalletSigner Signer { get; }


    public SpendableArkCoinWithSigner(ArkContract contract,
        DateTimeOffset? expiresAt,
        uint? expiresAtHeight,
        OutPoint outpoint,
        TxOut txout,
        IArkadeWalletSigner signer,
        OutputDescriptor signerDescriptor,
        ScriptBuilder spendingScriptBuilder,
        WitScript? spendingConditionWitness,
        LockTime? lockTime,
        Sequence? sequence, bool recoverable) : base(contract, expiresAt, expiresAtHeight, outpoint, txout, spendingScriptBuilder,
        spendingConditionWitness, lockTime, sequence, recoverable)
    {
        SignerDescriptor = signerDescriptor;
        Signer = signer;
    }
    
    internal SpendableArkCoinWithSigner(SpendableArkCoinWithSigner other) : this(
        other.Contract, other.ExpiresAt, other.ExpiresAtHeight, other.Outpoint.Clone(), other.TxOut.Clone(), other.Signer, other.SignerDescriptor,
        other.SpendingScriptBuilder, other.SpendingConditionWitness?.Clone(), other.SpendingLockTime, other.SpendingSequence,
        other.Recoverable)
    {
    }

    public async Task SignAndFillPSBT(
        PSBT psbt,
        TaprootReadyPrecomputedTransactionData precomputedTransactionData,
        TaprootSigHash sigHash = TaprootSigHash.Default,
        CancellationToken cancellationToken = default)
    {
        var psbtInput = FillPSBTInput(psbt);
        if (psbtInput is null)
        {
            return;
        }

        var gtx = psbt.GetGlobalTransaction();
        var hash = gtx.GetSignatureHashTaproot(precomputedTransactionData,
            new TaprootExecutionData((int) psbtInput.Index, SpendingScript.LeafHash)
            {
                SigHash = sigHash
            });
        
        var (sig, ourKey) = await Signer.Sign(hash, SignerDescriptor, cancellationToken);

        psbtInput.SetTaprootScriptSpendSignature(ourKey, SpendingScript.LeafHash, sig);
    }
}