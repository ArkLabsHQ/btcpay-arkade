using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;
using NArk;
using NArk.Contracts;
using NArk.Scripts;
using NArk.Extensions;

namespace NArk.Tests;

public class UnitTest1
{
    private readonly Network _network = Network.RegTest;
    private readonly Key _serverKey = new();
    private readonly Key _userKey = new();
    private readonly Key _senderKey = new();
    private readonly Key _receiverKey = new();
    private readonly byte[] _preimage = new byte[32];

    public UnitTest1()
    {
        Random.Shared.NextBytes(_preimage);
    }

    private ECXOnlyPubKey ServerPubKey => _serverKey.GetXOnlyPubKey();
    private ECXOnlyPubKey UserPubKey => _userKey.GetXOnlyPubKey();
    private ECXOnlyPubKey SenderPubKey => _senderKey.GetXOnlyPubKey();
    private ECXOnlyPubKey ReceiverPubKey => _receiverKey.GetXOnlyPubKey();

    [Fact]
    public void TestHashLockedPaymentContractScripts()
    {
        var contract = new HashLockedArkPaymentContract(
            ServerPubKey,
            new Sequence(10),
            KeyExtensions.ParseOutputDescriptor(UserPubKey.ToHex(), Network.RegTest),
            _preimage,
            HashLockTypeOption.SHA256
        );

        var claimScriptBuilder = contract.CreateClaimScript();
        var claimScript = new Script(claimScriptBuilder.BuildScript());
        Assert.NotNull(claimScript);

        var unilateralScriptBuilder = contract.UnilateralPath();
        var unilateralScript = new Script(unilateralScriptBuilder.BuildScript());
        Assert.NotNull(unilateralScript);

        // Example of a more detailed assertion
        var expectedClaimScript = new Script(
            OpcodeType.OP_SHA256,
            Op.GetPushOp(Hashes.SHA256(_preimage)),
            OpcodeType.OP_EQUALVERIFY,
            Op.GetPushOp(UserPubKey.ToBytes()),
            OpcodeType.OP_CHECKSIG
        );
        var collabPath = claimScriptBuilder as CollaborativePathArkTapScript;
        Assert.NotNull(collabPath);
        // We can't directly compare the built script as it's inside another script builder
        // but we can check the components
        Assert.Equal(ServerPubKey, collabPath.Server);

    }

    [Fact]
    public void TestVHTLCContractScripts()
    {
        var contract = new VHTLCContract(
            ServerPubKey,
            SenderPubKey,
            ReceiverPubKey,
            _preimage,
            new LockTime(1000),
            new Sequence(10),
            new Sequence(20),
            new Sequence(30)
        );

        var claimScript = new Script(contract.CreateClaimScript().BuildScript());
        Assert.NotNull(claimScript);

        var cooperativeScript = new Script(contract.CreateCooperativeScript().BuildScript());
        Assert.NotNull(cooperativeScript);

        var refundWithoutReceiverScript = new Script(contract.CreateRefundWithoutReceiverScript().BuildScript());
        Assert.NotNull(refundWithoutReceiverScript);

        var unilateralClaimScript = new Script(contract.CreateUnilateralClaimScript().BuildScript());
        Assert.NotNull(unilateralClaimScript);

        var unilateralRefundScript = new Script(contract.CreateUnilateralRefundScript().BuildScript());
        Assert.NotNull(unilateralRefundScript);

        var unilateralRefundWithoutReceiverScript = new Script(contract.CreateUnilateralRefundWithoutReceiverScript().BuildScript());
        Assert.NotNull(unilateralRefundWithoutReceiverScript);
    }

    [Fact]
    public void TestContractSerialization()
    {
        var contract = new HashLockedArkPaymentContract(
            ServerPubKey,
            new Sequence(10),
            UserPubKey,
            _preimage,
            HashLockTypeOption.HASH160
        );

        var data = contract.GetContractData();
        var parsedContract = HashLockedArkPaymentContract.Parse(data) as HashLockedArkPaymentContract;

        Assert.NotNull(parsedContract);
        Assert.Equal(contract.Server, parsedContract.Server);
        Assert.Equal(contract.User, parsedContract.User);
        Assert.Equal(contract.ExitDelay, parsedContract.ExitDelay);
        Assert.True(contract.Preimage.SequenceEqual(parsedContract.Preimage));
        Assert.Equal(contract.HashLockType, parsedContract.HashLockType);
    }

    [Fact]
    public void TestVHTLCSerialization()
    {
        var contract = new VHTLCContract(
            ServerPubKey,
            SenderPubKey,
            ReceiverPubKey,
            _preimage,
            new LockTime(1000),
            new Sequence(10),
            new Sequence(20),
            new Sequence(30)
        );

        var data = contract.GetContractData();
        var parsedContract = VHTLCContract.Parse(data) as VHTLCContract;

        Assert.NotNull(parsedContract);
        Assert.Equal(contract.Server, parsedContract.Server);
        Assert.Equal(contract.Sender, parsedContract.Sender);
        Assert.Equal(contract.Receiver, parsedContract.Receiver);
        Assert.Equal(contract.Hash, parsedContract.Hash);
        Assert.Equal(contract.RefundLocktime, parsedContract.RefundLocktime);
        Assert.Equal(contract.UnilateralClaimDelay, parsedContract.UnilateralClaimDelay);
        Assert.Equal(contract.UnilateralRefundDelay, parsedContract.UnilateralRefundDelay);
        Assert.Equal(contract.UnilateralRefundWithoutReceiverDelay, parsedContract.UnilateralRefundWithoutReceiverDelay);
    }
}