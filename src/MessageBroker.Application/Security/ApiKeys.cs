using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace MessageBroker.Application.Security;

/// <summary>A newly issued key. <see cref="Key"/> is shown to the caller once and never stored.</summary>
public sealed record IssuedApiKey(string Key, string Prefix, byte[] Hash);

/// <summary>
/// API keys look like <c>mbk_k3x9q2w7z8ab_&lt;43 base64url chars&gt;</c>. The first 16 characters are the
/// lookup prefix stored in broker.ApiKeys; only the SHA-256 hash of the whole key is stored with it.
/// </summary>
public static class ApiKeys
{
    public const string Scheme = "ApiKey";
    private const string Marker = "mbk_";
    private const int PrefixLength = 16;          // "mbk_" + 12 random characters
    private const int SecretBytes = 32;
    private const string PrefixAlphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    public static IssuedApiKey Generate()
    {
        var prefix = Marker + RandomNumberGenerator.GetString(PrefixAlphabet, PrefixLength - Marker.Length);
        var key = $"{prefix}_{Base64Url(RandomNumberGenerator.GetBytes(SecretBytes))}";
        return new IssuedApiKey(key, prefix, Hash(key));
    }

    /// <summary>Extracts the lookup prefix, or false when the text cannot be a broker key.</summary>
    public static bool TryGetPrefix(string? key, [NotNullWhen(true)] out string? prefix)
    {
        prefix = null;
        if (key is null || key.Length < PrefixLength + 2 || key.Length > 200
            || !key.StartsWith(Marker, StringComparison.Ordinal) || key[PrefixLength] != '_')
            return false;
        prefix = key[..PrefixLength];
        return true;
    }

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>Constant-time comparison of a presented key with a stored hash.</summary>
    public static bool Verify(string presentedKey, byte[] storedHash) =>
        CryptographicOperations.FixedTimeEquals(Hash(presentedKey), storedHash);

    /// <summary>A random secret for signing webhooks, as base64url text.</summary>
    public static string NewWebhookSecret() => Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
