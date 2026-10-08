using Samples.Shared;

namespace ConsoleSubscriber;

// dotnet run --project samples/ConsoleSubscriber   (webhook endpoint on Samples:ConsoleWebhook:Url, default http://localhost:5084)
// Run "SamplePublisher setup-console --AdminKey <key>" first.
// An explicit entry point: the Web SDK would otherwise make a public global Program class, which
// clashes with the broker's when the tests reference both.
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddSampleSettings().AddEnvironmentVariables().AddCommandLine(args);
        var settings = builder.Configuration.GetSampleSettings();
        if (ConsoleChannels.All.Any(c => string.IsNullOrEmpty(c.App(settings).ApiKey)))
        {
            Console.Error.WriteLine("No console subscriber keys. Run 'dotnet run --project samples/SamplePublisher -- setup-console --AdminKey <key>' first.");
            return 1;
        }

        builder.WebHost.UseUrls(settings.ConsoleWebhook.Url);
        builder.Services.AddConsoleSubscriber(settings);
        builder.Services.AddSingleton(sp => new NotificationPrinter(Console.Out, sp.GetRequiredService<TimeProvider>()));

        var app = builder.Build();
        app.MapNotificationWebhook();
        app.Services.GetRequiredService<NotificationPrinter>().Attach(app.Services);

        Console.WriteLine($"ConsoleSubscriber ← topic '{settings.NotificationsTopicName}' on {settings.BrokerUrl}");
        Console.WriteLine($"Listening over SignalR, Pull and Webhook ({settings.ConsoleWebhook.Url.TrimEnd('/')}{NotificationWebhook.Path}). Ctrl+C to stop.");
        await app.RunAsync();
        return 0;
    }
}
