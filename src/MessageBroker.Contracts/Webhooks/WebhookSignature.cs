using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MessageBroker.Contracts.Webhooks;

/// <summary>The X-Broker-* headers on every webhook request (spec section 9).</summary>
public static class WebhookHeaders
{
    public const string DeliveryId = "X-Broker-Delivery-Id";
    public const string LockToken = "X-Broker-Lock-Token";
    public const string Attempt = "X-Broker-Attempt";
    public const string Timestamp = "X-Broker-Timestamp";
    public const string Signature = "X-Broker-Signature";
}

/// <summary>
/// HMAC-SHA256 webhook signatures: <c>sha256=</c> + lowercase hex of HMAC(secret, timestamp + "." + body).
/// While a secret is being rotated the header carries both signatures, comma-separated
/// (<c>sha256=&lt;new&gt;,sha256=&lt;old&gt;</c>); a receiver accepts the request if either matches a secret it holds.
/// </summary>
public static class WebhookSignature
{
    public const string Prefix = "sha256=";
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    public static string Sign(string secret, string timestamp, string body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return Prefix + Convert.ToHexStringLower(hash);
    }

    /// <summary>The header value for one or more secrets (current first).</summary>
    public static string CreateHeader(string timestamp, string body, params IEnumerable<string> secrets) =>
        string.Join(",", secrets.Select(s => Sign(s, timestamp, body)));

    /// <summary>
    /// Verifies a request in constant time per signature, and rejects a timestamp more than
    /// <paramref name="tolerance"/> (default 5 minutes) away from <paramref name="now"/>.
    /// </summary>
    /// <param name="secrets">Secrets the receiver accepts; pass the old and the new one during rotation.</param>
    public static bool Verify(
        string? signatureHeader, string? timestamp, string body, DateTimeOffset now,
        IEnumerable<string> secrets, TimeSpan? tolerance = null)
    {
        if (string.IsNullOrEmpty(signatureHeader)
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds))
            return false;

        var age = now - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if (age.Duration() > (tolerance ?? DefaultTolerance))
            return false;

        var presented = signatureHeader.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(Encoding.UTF8.GetBytes).ToList();
        var matched = false;
        foreach (var secret in secrets)
        {
            var expected = Encoding.UTF8.GetBytes(Sign(secret, timestamp!, body));
            foreach (var candidate in presented)
                matched |= CryptographicOperations.FixedTimeEquals(expected, candidate);
        }
        return matched;
    }

    public static string Timestamp(DateTimeOffset now) => now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}
