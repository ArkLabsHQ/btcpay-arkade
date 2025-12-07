using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace NArk.Extensions;

public static class KeyExtensions
{
    public static ECPrivKey GetKeyFromWallet(string wallet, int index = -1)
    {
        switch (wallet.ToLowerInvariant())
        {
            case { } s1 when s1.StartsWith("nseed"):
                var encoder1 = Bech32Encoder.ExtractEncoderFromString(wallet);
                encoder1.StrictLength = false;
                encoder1.SquashBytes = true;
                var keyData1 = encoder1.DecodeDataRaw(wallet, out _);
                return index switch
                {
                    -1 => ExtKey.CreateFromSeed(keyData1).PrivateKey.ToKey(),
                    _ => ExtKey.CreateFromSeed(keyData1).Derive((uint)index).PrivateKey.ToKey()
                };
            case { } s2 when s2.StartsWith("nsec"):
                var encoder2 = Bech32Encoder.ExtractEncoderFromString(wallet);
                encoder2.StrictLength = false;
                encoder2.SquashBytes = true;
                var keyData2 = encoder2.DecodeDataRaw(wallet, out _);
                return ECPrivKey.Create(keyData2);
            default:
                throw new NotSupportedException();
        }
    }

    public static ECXOnlyPubKey GetXOnlyPubKeyFromWallet(string wallet, int index = -1)
    {
        switch (wallet.ToLowerInvariant())
        {
            case { } s1 when s1.StartsWith("npub"):
                var encoder = Bech32Encoder.ExtractEncoderFromString(wallet);
                encoder.StrictLength = false;
                encoder.SquashBytes = true;
                var keyData = encoder.DecodeDataRaw(wallet, out _);
                return ECXOnlyPubKey.Create(keyData);
            case { } s2 when s2.StartsWith("nsec"):
                var encoder2 = Bech32Encoder.ExtractEncoderFromString(wallet);
                encoder2.StrictLength = false;
                encoder2.SquashBytes = true;
                var keyData2 = encoder2.DecodeDataRaw(wallet, out _);
                return ECPrivKey.Create(keyData2).CreateXOnlyPubKey();
            case { } s2 when s2.StartsWith("nseed"):
                var encoder3 = Bech32Encoder.ExtractEncoderFromString(wallet);
                encoder3.StrictLength = false;
                encoder3.SquashBytes = true;
                var keyData3 = encoder3.DecodeDataRaw(wallet, out _);
                return index == -1
                    ? ExtKey.CreateFromSeed(keyData3).PrivateKey.GetXOnlyPubKey()
                    : ExtKey.CreateFromSeed(keyData3).Derive((uint)index).PrivateKey.GetXOnlyPubKey();
            default:
                throw new NotSupportedException();
        }
    }


    public static ECXOnlyPubKey ToECXOnlyPubKey(this string pubKeyHex)
    {
        var pubKey = new PubKey(pubKeyHex);
        return pubKey.ToECXOnlyPubKey();
    }

    public static ECXOnlyPubKey ToECXOnlyPubKey(this byte[] pubKeyBytes)
    {
        var pubKey = new PubKey(pubKeyBytes);
        return pubKey.ToECXOnlyPubKey();
    }

    public static ECXOnlyPubKey ToECXOnlyPubKey(this PubKey pubKey)
    {
        var xOnly = pubKey.TaprootInternalKey.ToBytes();
        return ECXOnlyPubKey.Create(xOnly);
    }

    public static string ToHex(this ECXOnlyPubKey value)
    {
        return Convert.ToHexString(value.ToBytes()).ToLowerInvariant();
    }    
    public static string ToHex(this ECPubKey value)
    {
        return Convert.ToHexString(value.ToBytes()).ToLowerInvariant();
    }
    
    public static Key ToKey(this ECPrivKey key)
    {
        var bytes = new Span<byte>();
        key.WriteToSpan(bytes);
        return new Key(bytes.ToArray());
    }
    public static ECPrivKey ToKey(this Key key)
    {
        return ECPrivKey.Create(key.ToBytes());
    }

    public static ECXOnlyPubKey GetXOnlyPubKey(this Key key)
    {
        return key.ToKey().CreateXOnlyPubKey();
    }

}