using NArk.Extensions;
using NArk.Scripts;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Contracts;

public class ArkPaymentContract : ArkContract
{
    private readonly Sequence _exitDelay;

    /// <summary>
    /// Output descriptor for the user key.
    /// </summary>
    public OutputDescriptor User { get; }

    public override string Type => ContractType;
    public const string ContractType = "Payment";
    

    public ArkPaymentContract(OutputDescriptor server, Sequence exitDelay, OutputDescriptor userDescriptor)
        : base(server)
    {
        _exitDelay = exitDelay;
        User = userDescriptor;
    }

    public override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        return [
            CollaborativePath(),
            UnilateralPath()
        ];
    }

    public ScriptBuilder CollaborativePath()
    {
        var ownerScript = new NofNMultisigTapScript([User.ToXOnlyPubKey()]);
        return new CollaborativePathArkTapScript(Server!.ToXOnlyPubKey(), ownerScript);
    }

    public ScriptBuilder UnilateralPath()
    {
        var ownerScript = new NofNMultisigTapScript([User.ToXOnlyPubKey()]);
        return new UnilateralPathArkTapScript(_exitDelay, ownerScript);
    }

    public WitScript UnilateralPathWitness(SecpSchnorrSignature server, SecpSchnorrSignature user)
    {
        var tapLeaf = UnilateralPath().Build();

        return new WitScript(
            Op.GetPushOp(server.ToBytes()),
            Op.GetPushOp(user.ToBytes()),
            Op.GetPushOp(tapLeaf.Script.ToBytes()),
            Op.GetPushOp(GetTaprootSpendInfo().GetControlBlock(tapLeaf).ToBytes()));
    }

    public override Dictionary<string, string> GetContractData()
    {
        var data = new Dictionary<string, string>
        {
            ["exit_delay"] = _exitDelay.Value.ToString(),
            ["user"] = User.ToString(),
            ["server"] = Server!.ToString()
        };
        return data;
    }

    public static ArkContract? Parse(Dictionary<string, string> contractData, Network network)
    {
        var server = KeyExtensions.ParseOutputDescriptor(contractData["server"], network);
        var exitDelay = new Sequence(uint.Parse(contractData["exit_delay"]));
        var userDescriptor = KeyExtensions.ParseOutputDescriptor(contractData["user"], network);
        return new ArkPaymentContract(server, exitDelay, userDescriptor);
    }
}
