namespace MessageBroker.Application.Services;

internal static class EnumNames
{
    /// <summary>Case-insensitive match on member names only; numeric text such as "0" is rejected.</summary>
    public static bool TryParse<T>(string? text, out T value) where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }
        value = default;
        return false;
    }
}
