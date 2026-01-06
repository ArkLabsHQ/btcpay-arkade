using System.Text;
using NArk.Abstractions.Wallets;

namespace BTCPayServer.Plugins.ArkPayServer.Wallet;

public static class WalletExtensions
{
    public static WalletType GetWalletType(this ArkWallet wallet)
    {
        var walletString = Encoding.UTF8.GetString(wallet.WalletPrivateBytes);

        // Check if it's a BIP-39 mnemonic (12 or 24 words)
        var words = walletString.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 12 || words.Length == 24)
        {
            return WalletType.HD;
        }

        // Check if it starts with nsec (legacy Nostr-style key)
        if (walletString.StartsWith("nsec", StringComparison.OrdinalIgnoreCase))
        {
            return WalletType.Legacy;
        }

        // Default to Legacy for backwards compatibility
        return WalletType.Legacy;
    }
}