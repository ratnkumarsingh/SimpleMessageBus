using Samples.Shared;

namespace WebhookSubscriber;

// An explicit entry point: the Web SDK would otherwise make a public global Program class, which
// clashes with the broker's when the tests reference both.
internal static class Program
{
    /// <summary>dotnet run --project samples/WebhookSubscriber (listens on Samples:Webhook:Url, default http://localhost:5081)</summary>
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddSampleSettings().AddEnvironmentVariables().AddCommandLine(args);
        var settings = builder.Configuration.GetSampleSettings();

        builder.WebHost.UseUrls(settings.Webhook.Url);
        builder.Services.AddPaymentSubscriber(settings, settings.Webhook, subscriber: "webhook");

        var app = builder.Build();
        await app.Services.GetRequiredService<SampleDatabase>().DeployAsync();
        app.MapPaymentWebhook();
        await app.RunAsync();
    }
}
