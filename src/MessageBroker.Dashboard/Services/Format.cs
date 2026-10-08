using System.Globalization;
using System.Text.Json;

namespace MessageBroker.Dashboard.Services;

/// <summary>Display helpers shared by the pages. Times are UTC throughout, and shown as UTC.</summary>
public static class Format
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>1,284 / 12.9K / 4.2M.</summary>
    public static string Compact(long value) => value switch
    {
        < 10_000 => value.ToString("N0", CultureInfo.InvariantCulture),
        < 1_000_000 => (value / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => (value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M",
    };

    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Utc(DateTime? value) =>
        value is { } v ? v.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "—";

    public static string Time(DateTime value) => value.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>"12 s ago", "4 min ago", "3 h ago", "2 d ago".</summary>
    public static string Ago(DateTime value, DateTime now)
    {
        var age = now - value;
        return age.TotalSeconds switch
        {
            < 1 => "just now",
            < 60 => $"{(int)age.TotalSeconds} s ago",
            < 3600 => $"{(int)age.TotalMinutes} min ago",
            < 86400 => $"{(int)age.TotalHours} h ago",
            _ => $"{(int)age.TotalDays} d ago",
        };
    }

    public static string Duration(int? milliseconds) => milliseconds switch
    {
        null => "—",
        < 1000 => $"{milliseconds} ms",
        _ => (milliseconds.Value / 1000d).ToString("0.0#", CultureInfo.InvariantCulture) + " s",
    };

    /// <summary>
    /// The last 8 hex digits of a GUID, enough to tell rows apart. Not the first block: message IDs are
    /// UUIDv7, which start with a timestamp, so messages published together share it.
    /// </summary>
    public static string Short(Guid id) => id.ToString("N")[^8..];

    public static string Json(JsonElement? element) =>
        element is { ValueKind: not JsonValueKind.Undefined } e ? JsonSerializer.Serialize(e, Indented) : "";

    /// <summary>The host of a webhook URL; the dashboard never shows paths or query strings.</summary>
    public static string Host(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : "—";

    /// <summary>A datetime-local input value (yyyy-MM-ddTHH:mm) read as UTC.</summary>
    public static DateTime? ParseUtc(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;

    public static string? InputUtc(DateTime? value) =>
        value?.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
}
