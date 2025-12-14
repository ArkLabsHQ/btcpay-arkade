using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Scripts;

public class CollaborativePathArkTapScript(ECXOnlyPubKey? server, ScriptBuilder? condition = null) : ScriptBuilder
{
    public ECXOnlyPubKey Server => server;
    public ScriptBuilder? Condition => condition;

    public static TapScript Create(ECXOnlyPubKey server, ScriptBuilder? condition = null) => 
        new CollaborativePathArkTapScript(server, condition).Build();

    public override IEnumerable<Op> BuildScript()
    {
        var condition = Condition?.BuildScript()?.ToList() ?? [];
        foreach (var op in condition)
        {
            yield return op;
        }
        yield return Op.GetPushOp(Server.ToBytes());
        yield return OpcodeType.OP_CHECKSIG;
    }
}