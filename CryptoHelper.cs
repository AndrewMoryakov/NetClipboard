using System.Security.Cryptography;
using System.Text;

namespace NetClipboard;

public static class CryptoHelper
{
    private const int SaltSize = 16;
    private const int NonceSize = 12; // AES-GCM standard
    private const int TagSize = 16;   // AES-GCM standard
    private const int KeySize = 32;   // AES-256
    private const int Iterations = 100_000;

    /// <summary>
    /// Encrypts plaintext with a password using PBKDF2 + AES-256-GCM.
    /// Returns Base64(salt16 + nonce12 + tag16 + ciphertext).
    /// </summary>
    public static string Encrypt(string plaintext, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt,
            Iterations, HashAlgorithmName.SHA256, KeySize);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Pack: salt + nonce + tag + ciphertext
        var result = new byte[SaltSize + NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(salt, 0, result, 0, SaltSize);
        Buffer.BlockCopy(nonce, 0, result, SaltSize, NonceSize);
        Buffer.BlockCopy(tag, 0, result, SaltSize + NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, result, SaltSize + NonceSize + TagSize, ciphertext.Length);

        return Convert.ToBase64String(result);
    }

    /// <summary>
    /// Decrypts a Base64-encoded ciphertext with a password.
    /// Returns null if the password is wrong.
    /// </summary>
    public static string? Decrypt(string cipherBase64, string password)
    {
        try
        {
            var data = Convert.FromBase64String(cipherBase64);
            if (data.Length < SaltSize + NonceSize + TagSize)
                return null;

            var salt = data.AsSpan(0, SaltSize).ToArray();
            var nonce = data.AsSpan(SaltSize, NonceSize).ToArray();
            var tag = data.AsSpan(SaltSize + NonceSize, TagSize).ToArray();
            var ciphertext = data.AsSpan(SaltSize + NonceSize + TagSize).ToArray();

            var key = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt,
                Iterations, HashAlgorithmName.SHA256, KeySize);

            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
