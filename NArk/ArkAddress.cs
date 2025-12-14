using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace NArk;

public class ArkAddress: TaprootPubKey
{
    protected static Bech32Encoder TestnetEncoder { get; set; }
    protected static readonly Bech32Encoder MainnetEncoder;
    protected static string HrpMainnet => "ark";
    protected static string HrpTestnet => "tark";

    static ArkAddress()
    {
        MainnetEncoder = Encoders.Bech32(HrpMainnet);
        MainnetEncoder.StrictLength = false;
        MainnetEncoder.SquashBytes = true;
        
        TestnetEncoder = Encoders.Bech32(HrpTestnet);
        TestnetEncoder.StrictLength = false;
        TestnetEncoder.SquashBytes = true;
    }

    public ArkAddress(TaprootAddress taprootAddress, ECXOnlyPubKey serverKey, int version = 0, Network? network = null) : base(taprootAddress.PubKey.ToBytes())
    {
        ArgumentNullException.ThrowIfNull(taprootAddress);
        ArgumentNullException.ThrowIfNull(serverKey);
        ArgumentNullException.ThrowIfNull(version);

        ServerKey = serverKey;
        Version = version;
        Network = network;
    }

    public ArkAddress(ECXOnlyPubKey tweakedKey, ECXOnlyPubKey serverKey, int version = 0, Network? network = null) : base(tweakedKey.ToBytes())
    {
        ArgumentNullException.ThrowIfNull(tweakedKey);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(serverKey);

        ServerKey = serverKey;
        Version = version;
        Network = network;
    }

    public ECXOnlyPubKey ServerKey { get; }
    public int Version { get; }
    private Network? Network { get; }

    public override string ToString()
    {
        return Network is null ? throw new InvalidOperationException("Network is required for address generation") : ToString(Network == Network.Main);
    }
    public string ToString(bool mainnet)
    {
        var encoder = mainnet ? MainnetEncoder : TestnetEncoder;
        byte[] bytes = [ Convert.ToByte(Version), ..ServerKey.ToBytes(), ..ToBytes() ];
        return encoder.EncodeData(bytes, Bech32EncodingType.BECH32M);
    }

    public static ArkAddress FromScriptPubKey(Script scriptPubKey, ECXOnlyPubKey serverKey)
    {
        var k = PayToTaprootTemplate.Instance.ExtractScriptPubKeyParameters(scriptPubKey);
        var pubKey = ECXOnlyPubKey.Create(k.ToBytes());
        return new ArkAddress(pubKey, serverKey);
    }

    public new static ArkAddress Parse(string address)
    {
        address = address.ToLowerInvariant();
     
        var encoder = address.StartsWith(HrpMainnet) ? MainnetEncoder :
            address.StartsWith(HrpTestnet) ? TestnetEncoder : throw new FormatException($"Invalid Ark address: {address}");
        var data = encoder.DecodeDataRaw(address, out var type);
        
        if (type != Bech32EncodingType.BECH32M || data.Length != 65)
            throw new FormatException($"Invalid Ark address: {address}");
        
        var version = data[0];
        var serverKey = ECXOnlyPubKey.Create(data.Skip(1).Take(32).ToArray());
        var tweakedKey = ECXOnlyPubKey.Create(data.Skip(33).ToArray());
        
        return new ArkAddress(tweakedKey, serverKey, version);
    }
    
    public static bool TryParse(string address, out ArkAddress? arkAddress)
    {
        try
        {
            arkAddress = Parse(address);
            return true;
        }
        catch (Exception)
        {
            arkAddress = null;
            return false;
        }
    }
}