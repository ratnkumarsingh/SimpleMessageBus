using Samples.Shared;

namespace ConsoleSubscriber;

/// <summary>Writes one line per received notification, tagged with the channel it came on.</summary>
public sealed class NotificationPrinter(TextWriter output, TimeProvider time)
{
    private readonly Lock _lock = new();

    /// <summary>Subscribes to every channel's feed in <paramref name="services"/>.</summary>
    public void Attach(IServiceProvider services)
    {
        foreach (var channel in ConsoleChannels.All)
            services.GetRequiredKeyedService<NotificationFeed>(channel.Name).Received += item => Print(channel.Name, item);
    }

    public void Print(string channel, ReceivedNotification item)
    {
        var line = Format(channel, item);
        // The three channels raise their feeds on different threads.
        lock (_lock)
        {
            var colored = ReferenceEquals(output, Console.Out);
            var previous = Console.ForegroundColor;
            if (colored)
                Console.ForegroundColor = ColorOf(item.Notification.Level);
            output.WriteLine(line);
            if (colored)
                Console.ForegroundColor = previous;
        }
    }

    /// <summary>
    /// <c>12:03:04 [SignalR] SUCCESS Deploy done: v1.2 is live (from ConsolePublisher, 35 ms after publish)</c>;
    /// the latency is left out when the clocks disagree.
    /// </summary>
    public string Format(string channel, ReceivedNotification item)
    {
        var n = item.Notification;
        var text = string.IsNullOrEmpty(n.Text) ? n.Title : $"{n.Title}: {n.Text}";
        var latency = item.ReceivedAt - item.PublishedAt;
        var after = latency >= TimeSpan.Zero ? $", {latency.TotalMilliseconds:0} ms after publish" : "";
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(item.ReceivedAt, DateTimeKind.Utc), time.LocalTimeZone);
        return $"{local:HH:mm:ss} [{channel}] {n.Level.ToString().ToUpperInvariant()} {text} (from {n.SentBy}{after})";
    }

    private static ConsoleColor ColorOf(NotificationLevel level) => level switch
    {
        NotificationLevel.Success => ConsoleColor.Green,
        NotificationLevel.Warning => ConsoleColor.Yellow,
        NotificationLevel.Error => ConsoleColor.Red,
        _ => ConsoleColor.Cyan,
    };
}
