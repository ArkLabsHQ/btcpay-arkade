using NArk.Extensions;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;

namespace NArk.Services;

/// <summary>
/// Service for BIP-86 Taproot HD key derivation.
/// Derivation path: m/86'/{coinType}'/0'/0/{index}
/// </summary>
public static class HDKeyDerivationService
{
    /// <summary>
    /// Creates wallet data from an existing mnemonic.
    /// </summary>
    /// <param name="mnemonicWords">BIP-39 mnemonic words</param>
    /// <param name="network">Bitcoin network</param>
    /// <param name="passphrase">Optional BIP-39 passphrase</param>
    /// <returns>Tuple of (mnemonic words, account descriptor)</returns>
    public static string ComputeAccountDescriptor(
        string mnemonicWords,
        Network network,
        string? passphrase = null)
    {
        var mnemonic = new Mnemonic(mnemonicWords);
        var extKey = mnemonic.DeriveExtKey(passphrase);
        var fingerprint = extKey.GetPublicKey().GetHDFingerPrint();
        var coinType = GetCoinType(network);

        // BIP-86 Taproot: m/86'/coin'/0'
        var accountKeyPath = new KeyPath($"m/86'/{coinType}'/0'");
        var accountXpriv = extKey.Derive(accountKeyPath);
        var accountXpub = accountXpriv.Neuter().GetWif(network).ToWif();

        // Descriptor format: tr([fingerprint/86'/coin'/0']xpub/0/*)
        var descriptor = $"tr([{fingerprint}/86'/{coinType}'/0']{accountXpub}/0/*)";

        return descriptor;
    }

    // /// <summary>
    // /// Derives a public key at a specific index from an account descriptor.
    // /// </summary>
    // /// <param name="accountDescriptor">Account descriptor in format tr([fp/path]xpub/0/*)</param>
    // /// <param name="index">Derivation index</param>
    // /// <param name="network">Bitcoin network for descriptor parsing</param>
    // /// <returns>X-only public key at the specified index</returns>
    // public static ECXOnlyPubKey DerivePublicKeyAtIndex(string accountDescriptor, int index, Network network)
    // {
    //     // Replace wildcard with specific index
    //     var indexedDescriptor = GetDescriptorAtIndex(accountDescriptor, index);
    //
    //     var parsed = OutputDescriptor.Parse(indexedDescriptor, network);
    //
    //     if (parsed is OutputDescriptor.Tr trDescriptor)
    //     {
    //         // Extract the inner public key from the taproot descriptor
    //         var pubKey = trDescriptor.InnerPubkey.GetPubKey(0, _ => null);
    //         return pubKey.ToECXOnlyPubKey();
    //     }
    //
    //     throw new InvalidOperationException($"Failed to parse taproot descriptor: {indexedDescriptor}");
    // }

    /// <summary>
    /// Derives a private key at a specific index from a mnemonic.
    /// </summary>
    /// <param name="mnemonicWords">BIP-39 mnemonic words</param>
    /// <param name="index">Derivation index</param>
    /// <param name="network">Bitcoin network</param>
    /// <param name="passphrase">Optional BIP-39 passphrase</param>
    /// <returns>EC private key at the specified index</returns>
    public static ECPrivKey DerivePrivateKeyAtIndex(
        string mnemonicWords,
        int index,
        Network network,
        string? passphrase = null)
    {
        var mnemonic = new Mnemonic(mnemonicWords);
        var extKey = mnemonic.DeriveExtKey(passphrase);
        var coinType = GetCoinType(network);

        // Full path: m/86'/coin'/0'/0/index
        var keyPath = new KeyPath($"m/86'/{coinType}'/0'/0/{index}");
        var derivedKey = extKey.Derive(keyPath);

        return derivedKey.PrivateKey.ToKey();
    }

    /// <summary>
    /// Gets the BIP-44 coin type for a network.
    /// </summary>
    private static string GetCoinType(Network network)
    {
        // Mainnet uses coin type 0, all testnets use coin type 1
        return network.ChainName == ChainName.Mainnet ? "0" : "1";
    }
    

    /// <summary>
    /// Gets the descriptor at a specific index (non-wildcard).
    /// </summary>
    /// <param name="accountDescriptor">Account descriptor with wildcard (e.g., tr([fp/path]xpub/0/*)</param>
    /// <param name="index">The specific index to derive</param>
    /// <returns>Descriptor with the specific index (e.g., tr([fp/path]xpub/0/5))</returns>
    public static OutputDescriptor GetDescriptorAtIndex(string accountDescriptor, int index, Network network)
    {
        // Replace wildcard with specific index
        return OutputDescriptor.Parse(accountDescriptor.Replace("/*", $"/{index}"), network);
    }
}
