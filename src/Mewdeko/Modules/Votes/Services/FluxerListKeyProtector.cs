using System.Security.Cryptography;
using System.Text;
using Mewdeko.Services.Impl;

namespace Mewdeko.Modules.Votes.Services;

/// <summary>
///     Encrypts and decrypts communities' FluxerList API keys with AES-GCM.
/// </summary>
public class FluxerListKeyProtector : INService
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] key;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FluxerListKeyProtector" /> class.
    /// </summary>
    /// <param name="creds">The bot credentials the encryption key comes from.</param>
    public FluxerListKeyProtector(IBotCredentials creds)
    {
        var secret = !string.IsNullOrWhiteSpace(creds.FluxerListKeyEncryptionKey)
            ? creds.FluxerListKeyEncryptionKey
            : creds is BotCredentials full
                ? $"{full.JwtSecret}|{full.ApiKey}|{creds.Token}"
                : creds.Token;

        key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), 32, null,
            "mewdeko-fluxerlist-key"u8.ToArray());
    }

    /// <summary>
    ///     Encrypts an API key.
    /// </summary>
    /// <param name="plainText">The API key.</param>
    /// <returns>The encrypted value as base64.</returns>
    public string Protect(string plainText)
    {
        var plain = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        var result = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, NonceSize);
        cipher.CopyTo(result, NonceSize + TagSize);
        return Convert.ToBase64String(result);
    }

    /// <summary>
    ///     Decrypts an API key.
    /// </summary>
    /// <param name="protectedText">The encrypted value as base64.</param>
    /// <returns>The API key, or null when it cannot be decrypted.</returns>
    public string? Unprotect(string protectedText)
    {
        try
        {
            var data = Convert.FromBase64String(protectedText);
            if (data.Length < NonceSize + TagSize)
                return null;

            var cipher = data.AsSpan(NonceSize + TagSize);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(data.AsSpan(0, NonceSize), cipher, data.AsSpan(NonceSize, TagSize), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
