using BTCPayServer.Plugins.ArkPayServer.Data.Entities;
using NArk;
using NArk.Abstractions.Wallets;
using NArk.Swaps.Helpers;
using NArk.Wallets;
using NBitcoin;
using NBitcoin.Scripting;
using ArkWallet = BTCPayServer.Plugins.ArkPayServer.Data.Entities.ArkWallet;

namespace BTCPayServer.Plugins.ArkPayServer.Wallet;

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
    public static async Task<ArkWallet> CreateWallet(
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
            return await CreateNsecWallet(walletSecret, destination, serverInfo, cancellationToken);
        }
        else
        {
            return CreateHdWallet(walletSecret, destination, serverInfo);
        }
    }

    // /// <summary>
    // /// Creates a signer from a wallet entity's stored secret.
    // /// </summary>
    // public static ISigningEntity? CreateSigner(ArkWallet wallet, Network network)
    // {
    //     if (string.IsNullOrEmpty(wallet.Wallet))
    //         return null;
    //
    //     try
    //     {
    //         if (wallet.WalletType == WalletType.Legacy && wallet.Wallet.StartsWith("nsec", StringComparison.OrdinalIgnoreCase))
    //         {
    //             return SingleKeySigningEntity.FromNsec(wallet.Wallet, network);
    //         }
    //         else if (wallet.WalletType == WalletType.HD)
    //         {
    //             return SimpleSeedWallet.FromMnemonic(wallet.Wallet, network);
    //         }
    //     }
    //     catch
    //     {
    //         // Failed to create signer - wallet may have invalid format
    //     }
    //
    //     return null;
    // }

    /// <summary>
    /// Validates a destination address against the server's key.
    /// </summary>
    public static void ValidateDestination(string destination, ArkServerInfo serverInfo)
    {
        var addr = ArkAddress.Parse(destination);
        var serverKey = OutputDescriptorHelpers.Extract(serverInfo.SignerKey).XOnlyPubKey;
        if (!serverKey.ToBytes().SequenceEqual(addr.ServerKey.ToBytes()))
        {
            throw new InvalidOperationException("Invalid destination server key.");
        }
    }

    private static async Task<ArkWallet> CreateNsecWallet(
        string nsec,
        string? destination,
        ArkServerInfo serverInfo,
        CancellationToken cancellationToken)
    {
        var signingEntity = SingleKeySigningEntity.FromNsec(nsec, serverInfo.Network);
        var publicKey = (await signingEntity.GetPublicKey(cancellationToken)).ToXOnlyPubKey();
        var walletId = Convert.ToHexString(publicKey.ToBytes()).ToLowerInvariant();

        var wallet = new ArkWallet
        {
            Id = walletId,
            WalletDestination = destination,
            Wallet = nsec,
            WalletType = WalletType.Legacy
        };

        return (wallet);
    }

    private static ArkWallet CreateHdWallet(
        string mnemonic,
        string? destination,
        ArkServerInfo serverInfo)
    {
        var mnemonicObj = new Mnemonic(mnemonic);
        var extKey = mnemonicObj.DeriveExtKey();
        var fingerprint = extKey.GetPublicKey().GetHDFingerPrint();
        var coinType = serverInfo.Network.ChainName == ChainName.Mainnet ? "0" : "1";

        // Derive wallet ID from fingerprint
        var walletId = fingerprint.ToString().ToLowerInvariant();

        // Create account descriptor
        var accountKeyPath = new KeyPath($"m/86'/{coinType}'/0'");
        var accountXpriv = extKey.Derive(accountKeyPath);
        var accountXpub = accountXpriv.Neuter().GetWif(serverInfo.Network).ToWif();
        var accountDescriptor = $"tr([{fingerprint}/86'/{coinType}'/0']{accountXpub}/0/*)";

        var wallet = new ArkWallet
        {
            Id = walletId,
            WalletDestination = destination,
            Wallet = mnemonic,
            WalletType = WalletType.HD,
            AccountDescriptor = accountDescriptor,
            LastUsedIndex = 0
        };
        return wallet;
    }
}
