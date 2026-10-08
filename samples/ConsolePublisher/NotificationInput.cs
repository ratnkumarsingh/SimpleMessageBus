using Samples.Shared;

namespace ConsolePublisher;

/// <summary>
/// One line typed at the prompt: <c>Title | message | level</c>. The message and level are optional;
/// the level, when given, is the last part and defaults to Info. A message may itself contain '|' as
/// long as a level ends the line.
/// </summary>
public static class NotificationInput
{
    public const int MaxTitleLength = 80;
    public const int MaxTextLength = 500;
    public const string Syntax = "Title | message | level   (message and level are optional; level: Info, Success, Warning, Error)";

    public sealed record Result(UserNotification? Notification, string? Error);

    public static Result Parse(string? line, string sentBy)
    {
        var parts = (line ?? "").Split('|').Select(p => p.Trim()).ToArray();
        var title = parts[0];
        if (title.Length == 0)
            return new(null, "Type a title first.");
        if (title.Length > MaxTitleLength)
            return new(null, $"The title can be at most {MaxTitleLength} characters.");

        var level = NotificationLevel.Info;
        var text = "";
        if (parts.Length == 2)
        {
            text = parts[1];
        }
        else if (parts.Length >= 3)
        {
            if (!TryParseLevel(parts[^1], out level))
                return new(null, $"Unknown level '{parts[^1]}'. Use Info, Success, Warning or Error.");
            text = string.Join(" | ", parts[1..^1]);
        }

        if (text.Length > MaxTextLength)
            return new(null, $"The message can be at most {MaxTextLength} characters.");
        return new(new UserNotification(title, text, level, sentBy), null);
    }

    /// <summary>Names only, any case; numbers such as "1" are not levels.</summary>
    public static bool TryParseLevel(string? value, out NotificationLevel level)
    {
        foreach (var candidate in Enum.GetValues<NotificationLevel>())
        {
            if (string.Equals(candidate.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                level = candidate;
                return true;
            }
        }
        level = NotificationLevel.Info;
        return false;
    }
}
