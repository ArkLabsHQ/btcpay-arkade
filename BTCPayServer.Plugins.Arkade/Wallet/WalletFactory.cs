using NArk.Core;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Core.Extensions;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;
using ArkWallet = BTCPayServer.Plugins.Arkade.Data.Entities.ArkWallet;
using Entities_ArkWallet = BTCPayServer.Plugins.Arkade.Data.Entities.ArkWallet;

namespace BTCPayServer.Plugins.Arkade.Wallet;

/// <summary>
/// Factory for creating wallet entities and signers from secrets (nsec or mnemonic).
/// </summary>
public static class WalletFactory
{
    /// <summary>
    /// Creates a wallet entity and optional signer from a wallet secret.
    /// </summary>
    /// <param name="walletSecret">The wallet secret (nsec or BIP-39 mnemonic)</param>
    /// <param name="destination">Optional destination address for auto-sweep</param>
    /// <param name="serverInfo">Server info for network and validation</param>
    /// <returns>Tuple of (wallet entity, signer if applicable)</returns>
    public static async Task<Entities_ArkWallet> CreateWallet(
        string walletSecret,
        string? destination,
        ArkServerInfo serverInfo,
        CancellationToken cancellationToken = default)
    {
        // Validate destination if provided
        if (destination is not null)
        {
            ValidateDestination(destination, serverInfo);
        }

        if (walletSecret.StartsWith("nsec", StringComparison.OrdinalIgnoreCase))
        {
            return await CreateNsecWallet(walletSecret, destination);
        }

        return CreateHdWallet(walletSecret, destination, serverInfo);
    }

    /// <summary>
    /// Validates a destination address against the server's key.
    /// </summary>
    public static void ValidateDestination(string destination, ArkServerInfo serverInfo)
    {
        var addr = ArkAddress.Parse(destination);
        var serverKey = serverInfo.SignerKey.Extract().XOnlyPubKey;
        if (!serverKey.ToBytes().SequenceEqual(addr.ServerKey.ToBytes()))
        {
            throw new InvalidOperationException("Invalid destination server key.");
        }
    }

    private static async Task<Entities_ArkWallet> CreateNsecWallet(
        string nsec,
        string? destination)
    {
        var outputDescriptor = GetOutputDescriptorFromNsec(nsec);
        var wallet = new Entities_ArkWallet
        {
            Id = outputDescriptor,
            WalletDestination = destination,
            Wallet = nsec,
            WalletType = WalletType.SingleKey,
            AccountDescriptor = outputDescriptor,
            LastUsedIndex = 0
        };

        return wallet;
    }

    public static string GetOutputDescriptorFromNsec(string nsec)
    {
        var encoder2 = Bech32Encoder.ExtractEncoderFromString(nsec);
        encoder2.StrictLength = false;
        encoder2.SquashBytes = true;
        var keyData2 = encoder2.DecodeDataRaw(nsec, out _);
        var privKey = ECPrivKey.Create(keyData2);
        
        var outputDescriptor = $"tr({privKey.CreatePubKey().ToBytes().ToHexStringLower()})";
        return outputDescriptor;
    }

    private static Entities_ArkWallet CreateHdWallet(
        string mnemonic,
        string? destination,
        ArkServerInfo serverInfo)
    {
        var mnemonicObj = new Mnemonic(mnemonic);
        var extKey = mnemonicObj.DeriveExtKey();
        var fingerprint = extKey.GetPublicKey().GetHDFingerPrint();
        var coinType = serverInfo.Network.ChainName == ChainName.Mainnet ? "0" : "1";

        // Create account descriptor
        var accountKeyPath = new KeyPath($"m/86'/{coinType}'/0'");
        var accountXpriv = extKey.Derive(accountKeyPath);
        var accountXpub = accountXpriv.Neuter().GetWif(serverInfo.Network).ToWif();
        var accountDescriptor = $"tr([{fingerprint}/86'/{coinType}'/0']{accountXpub}/0/*)";

        var wallet = new Entities_ArkWallet
        {
            Id = accountDescriptor,
            WalletDestination = destination,
            Wallet = mnemonic,
            WalletType = WalletType.HD,
            AccountDescriptor = accountDescriptor,
            LastUsedIndex = 0
        };

        return wallet;
    }
}
