namespace STS2Mobile.Steam.Abstractions;

/// <summary>Encrypts small secrets at rest. Both methods return null instead of an unprotected value.</summary>
public interface ISecureStorage
{
    string? Encrypt(string plaintext);

    string? Decrypt(string ciphertext);

    /// <summary>Deletes the key, so nothing encrypted with it can be read again. Throws <see cref="InvalidOperationException"/> when the key store fails.</summary>
    void DeleteKey();
}
