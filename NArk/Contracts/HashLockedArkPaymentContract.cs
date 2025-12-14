using NArk.Extensions;
using NArk.Scripts;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Contracts;

public class HashLockedArkPaymentContract : ArkContract
{
    private readonly Sequence _exitDelay;
    private readonly byte[] _preimage;
    private readonly HashLockTypeOption _hashLockType;

    /// <summary>
    /// Output descriptor for the user key. Can be null for special contracts like ArkNoteContract.
    /// </summary>
    public OutputDescriptor? User { get; }
    

    public byte[] Hash
    {
        get
        {
            return _hashLockType switch
            {
                HashLockTypeOption.HASH160 => Hashes.Hash160(_preimage).ToBytes(),
                HashLockTypeOption.SHA256 => Hashes.SHA256(_preimage),
                _ => throw new ArgumentOutOfRangeException()
            };
        }
    }

    public override string Type => ContractType;
    public const string ContractType = "HashLockPaymentContract";
    public byte[] Preimage => _preimage;
    public Sequence ExitDelay => _exitDelay;
    public HashLockTypeOption HashLockType => _hashLockType;


    public HashLockedArkPaymentContract(OutputDescriptor? server, Sequence exitDelay, OutputDescriptor? user, byte[] preimage, HashLockTypeOption hashLockType)
        : base(server)
    {
        _exitDelay = exitDelay;
        _preimage = preimage;
        _hashLockType = hashLockType;
        User = user;
    }

    public override Dictionary<string, string> GetContractData()
    {
        var data = new Dictionary<string, string>
        {
            ["exit_delay"] = _exitDelay.Value.ToString(),
            ["preimage"] = _preimage.ToHex(),
            ["hash_lock_type"] = Enum.GetName(_hashLockType) ?? throw new ArgumentOutOfRangeException(nameof(_hashLockType), "Invalid hash lock type")
        };

        if (User != null)
            data["user"] = User.ToString();
        if (Server != null)
            data["server"] = Server.ToString();

        return data;
    }

    public override IEnumerable<ScriptBuilder> GetScriptBuilders()
    {
        return [
            CreateClaimScript(),
            UnilateralPath()
        ];
    }

    public ScriptBuilder CreateClaimScript()
    {
        var hashLock = new HashLockTapScript(Hash, _hashLockType);
        var receiverMultisig = new NofNMultisigTapScript([User?.ToXOnlyPubKey() ?? throw new InvalidOperationException("User is required for claim script generation")]);
        return new CollaborativePathArkTapScript(Server.ToXOnlyPubKey(),
            new CompositeTapScript(hashLock, new VerifyTapScript(), receiverMultisig));
    }

    public ScriptBuilder UnilateralPath()
    {
        var ownerScript = new NofNMultisigTapScript([User?.ToXOnlyPubKey()  ?? throw new InvalidOperationException("User is required for unilateral script generation")]);
        return new UnilateralPathArkTapScript(_exitDelay, ownerScript);
    }

    public static ArkContract? Parse(Dictionary<string, string> contractData, Network network)
    {
        var server = KeyExtensions.ParseOutputDescriptor(contractData["server"], network);
        var exitDelay = new Sequence(uint.Parse(contractData["exit_delay"]));
        var userDescriptor = contractData.TryGetValue("user", out var userStr)
            ? KeyExtensions.ParseOutputDescriptor(userStr, network)
            : null;
        var preimage = Convert.FromHexString(contractData["preimage"]);
        var hashLockType = Enum.Parse<HashLockTypeOption>(contractData["hash_lock_type"]);
        return new HashLockedArkPaymentContract(server, exitDelay, userDescriptor, preimage, hashLockType);
    }
}
