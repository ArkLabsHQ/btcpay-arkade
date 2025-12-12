using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace NArk.Extensions;

public static class KeyExtensions
{
    public static ECPrivKey GetKeyFromWallet(string wallet)
    {
        switch (wallet.ToLowerInvariant())
        {
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

    public static ECXOnlyPubKey GetXOnlyPubKeyFromWallet(string wallet)
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