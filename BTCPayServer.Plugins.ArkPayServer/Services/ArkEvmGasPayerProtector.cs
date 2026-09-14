using System.Security.Cryptography;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using Microsoft.AspNetCore.DataProtection;
using Nethereum.Signer;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>Protects EVM gas-payer keys with a store-and-address-bound purpose.</summary>
public sealed class ArkEvmGasPayerProtector(IDataProtectionProvider provider)
{
    /// <summary>Validates the key/address pair and returns only ciphertext.</summary>
    public string Protect(string storeId, string? privateKey, string? expectedAddress)
    {
        var address = NormalizeAddress(expectedAddress);
        var key = ParseKey(privateKey);
        byte[]? ciphertext = null;
        try
        {
            if (!string.Equals(DeriveAddress(key), address, StringComparison.Ordinal))
                throw new ArgumentException("The EVM private key does not derive the configured sender address.");
            ciphertext = ForStore(storeId, address).Protect(key);
            return Convert.ToBase64String(ciphertext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    /// <summary>Reports whether ciphertext can be opened and still matches its configured address.</summary>
    public bool IsAvailable(string storeId, string? protectedKey, string? expectedAddress)
    {
        if (string.IsNullOrWhiteSpace(protectedKey)) return false;
        try
        {
            return UseKey(storeId, protectedKey, expectedAddress, static _ => true);
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Uses an unprotected key synchronously and zeroes its backing buffer afterward.</summary>
    public T UseKey<T>(string storeId, string protectedKey, string? expectedAddress,
        Func<ReadOnlyMemory<byte>, T> use)
    {
        ArgumentNullException.ThrowIfNull(use);
        var address = NormalizeAddress(expectedAddress);
        byte[]? encoded = null;
        byte[]? key = null;
        try
        {
            encoded = Convert.FromBase64String(protectedKey);
            key = ForStore(storeId, address).Unprotect(encoded);
            if (key.Length != 32 || !string.Equals(DeriveAddress(key), address, StringComparison.Ordinal))
                throw new CryptographicException("Protected EVM gas-payer key is invalid.");
            return use(key);
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (encoded is not null) CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private IDataProtector ForStore(string storeId, string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeId);
        return provider.CreateProtector("BTCPayServer.Plugins.ArkPayServer.EvmGasPayer.v1", storeId, address);
    }

    private static string NormalizeAddress(string? value)
    {
        if (!ArkEvmSettlementSettings.IsNonzeroAddress(value))
            throw new ArgumentException("Specify a nonzero EVM gas-payer address.");
        return value!.ToLowerInvariant();
    }

    private static byte[] ParseKey(string? value)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Specify a 32-byte EVM private key.");
        try
        {
            return Convert.FromHexString(value);
        }
        catch (FormatException)
        {
            throw new ArgumentException("Specify a 32-byte EVM private key.");
        }
    }

    private static string DeriveAddress(ReadOnlySpan<byte> key)
    {
        var privateCopy = key.ToArray();
        try
        {
            try
            {
                return new EthECKey(privateCopy, true).GetPublicAddress().ToLowerInvariant();
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                throw new ArgumentException("Specify a valid secp256k1 EVM private key.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateCopy);
        }
    }

    /// <inheritdoc />
    public override string ToString() => nameof(ArkEvmGasPayerProtector);
}
