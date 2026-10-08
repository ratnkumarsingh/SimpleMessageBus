using MessageBroker.Contracts.Client;
using Microsoft.Extensions.Configuration;
using Samples.Shared;

namespace ConsolePublisher;

// dotnet run --project samples/ConsolePublisher                       interactive: type "Title | message | level"
// dotnet run --project samples/ConsolePublisher -- --Count 5 [--IntervalSeconds 2] [--Level Success]
// Run "SamplePublisher setup-console --AdminKey <key>" first.
internal static class Program
{
    public const string SentBy = "ConsolePublisher";

    public static async Task<int> Main(string[] args)
    {
        var config = new ConfigurationBuilder().AddSampleSettings().AddEnvironmentVariables().AddCommandLine(args).Build();
        var settings = config.GetSampleSettings();
        if (string.IsNullOrEmpty(settings.ConsolePublisher.ApiKey))
        {
            Console.Error.WriteLine("No console publisher key. Run 'dotnet run --project samples/SamplePublisher -- setup-console --AdminKey <key>' first.");
            return 1;
        }

        using var http = new HttpClient { BaseAddress = settings.BrokerUrl, Timeout = TimeSpan.FromSeconds(30) };
        var sender = new NotificationSender(BrokerClient.Create(http, settings.ConsolePublisher.ApiKey), settings.NotificationsTopicName);
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        Console.WriteLine($"ConsolePublisher → topic '{settings.NotificationsTopicName}' on {settings.BrokerUrl}");
        return config.GetValue<int?>("Count") is { } count
            ? await AutoAsync(sender, count, config, stop.Token)
            : await InteractiveAsync(sender, stop.Token);
    }

    private static async Task<int> AutoAsync(NotificationSender sender, int count, IConfiguration config, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(config.GetValue("IntervalSeconds", 2.0));
        if (!NotificationInput.TryParseLevel(config["Level"] ?? "Info", out var level))
        {
            Console.Error.WriteLine("--Level must be Info, Success, Warning or Error.");
            return 1;
        }

        var failures = 0;
        for (var n = 1; n <= count && !ct.IsCancellationRequested; n++)
        {
            var notification = new UserNotification($"Console notification {n} of {count}",
                $"Sent at {DateTime.UtcNow:HH:mm:ss} UTC.", level, SentBy);
            if (!Report(await sender.SendAsync(notification, ct)))
                failures++;
            if (n < count)
                await Task.Delay(interval, ct).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> InteractiveAsync(NotificationSender sender, CancellationToken ct)
    {
        Console.WriteLine($"Type a notification and press Enter: {NotificationInput.Syntax}");
        Console.WriteLine("Commands: help, quit (or Ctrl+C).");
        while (!ct.IsCancellationRequested)
        {
            Console.Write("notify> ");
            var line = Console.ReadLine();
            if (line is null || ct.IsCancellationRequested)
                break;
            switch (line.Trim().ToLowerInvariant())
            {
                case "":
                    continue;
                case "quit" or "exit":
                    return 0;
                case "help" or "?":
                    Console.WriteLine(NotificationInput.Syntax);
                    Console.WriteLine("Example: Deploy done | v1.2 is live | Success");
                    continue;
            }

            var input = NotificationInput.Parse(line, SentBy);
            if (input.Notification is null)
            {
                Write(ConsoleColor.Yellow, input.Error!);
                continue;
            }
            Report(await sender.SendAsync(input.Notification, ct));
        }
        return 0;
    }

    private static bool Report(NotificationSender.Outcome outcome)
    {
        Write(outcome.Published ? ConsoleColor.Green : ConsoleColor.Red, outcome.Description);
        return outcome.Published;
    }

    private static void Write(ConsoleColor color, string text)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }
}
