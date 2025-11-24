using NBitcoin;

namespace NArk.Services;

public class IntentTxOut : TxOut
{
    public enum IntentOutputType
    {
        VTXO,
        OnChain
    }

    public IntentTxOut() {}

    public IntentTxOut(TxOut output, IntentOutputType type)
        : base(output.Value, output.ScriptPubKey)
    {
        Type = type;
    }

    public IntentOutputType Type { get; set; }
}