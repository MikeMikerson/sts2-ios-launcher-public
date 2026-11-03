using System;
using System.Security.Cryptography;
using System.Text;
using STS2Mobile.Platform;
using STS2Mobile.Steam.Abstractions;

namespace STS2Mobile.FullGame;

/// <summary>
/// Protects Steam credentials with AES-GCM under a key that only exists in the Apple Keychain
/// (device-only, unavailable while the phone is locked). Failures surface as exceptions or null,
/// never as a silently unprotected value.
/// </summary>
public sealed class KeychainSecureStorage : ISecureStorage
{
    private const string CredentialKeyAccount = "steam-storage-key-v1";
    // A stored value is the format version, the nonce and the tag, followed by the ciphertext.
    private const byte FormatVersion = 1;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int NonceOffset = 1;
    private const int TagOffset = NonceOffset + NonceSize;
    private const int HeaderSize = TagOffset + TagSize;
    private readonly AppleKeychain _keychain = new(AppleKeychain.ApplicationIdentifier() + ".steam");
    private readonly object _gate = new();

    public string? Encrypt(string plaintext)
    {
        lock (_gate)
        {
            byte[]? key = null;
            byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
            try
            {
                key = _keychain.Read(CredentialKeyAccount);
                if (key == null) { key = RandomNumberGenerator.GetBytes(KeySize); _keychain.Write(CredentialKeyAccount, key); }
                var result = new byte[HeaderSize + bytes.Length];
                result[0] = FormatVersion;
                RandomNumberGenerator.Fill(result.AsSpan(NonceOffset, NonceSize));
                using var aes = new AesGcm(key, TagSize);
                aes.Encrypt(result.AsSpan(NonceOffset, NonceSize), bytes, result.AsSpan(HeaderSize), result.AsSpan(TagOffset, TagSize));
                return Convert.ToBase64String(result);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                if (key != null) CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    public void DeleteKey()
    {
        lock (_gate) _keychain.Delete(CredentialKeyAccount);
    }

    public string? Decrypt(string ciphertext)
    {
        lock (_gate)
        {
            byte[]? key = null, plaintext = null;
            try
            {
                byte[] data = Convert.FromBase64String(ciphertext);
                if (data.Length < HeaderSize || data[0] != FormatVersion) return null;
                key = _keychain.Read(CredentialKeyAccount);
                if (key == null) return null;
                plaintext = new byte[data.Length - HeaderSize];
                using var aes = new AesGcm(key, TagSize);
                aes.Decrypt(data.AsSpan(NonceOffset, NonceSize), data.AsSpan(HeaderSize), data.AsSpan(TagOffset, TagSize), plaintext);
                return Encoding.UTF8.GetString(plaintext);
            }
            catch (FormatException) { return null; }
            catch (CryptographicException) { return null; }
            finally
            {
                if (key != null) CryptographicOperations.ZeroMemory(key);
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }
}
