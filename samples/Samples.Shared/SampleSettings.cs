using Microsoft.Extensions.Configuration;

namespace Samples.Shared;

/// <summary>The "Samples" configuration section every sample reads.</summary>
public sealed class SampleSettings
{
    public const string SectionName = "Samples";
    /// <summary>Written by "SamplePublisher setup" next to the samples, and read by every sample.</summary>
    public const string LocalFileName = "samples.local.json";

    public Uri BrokerUrl { get; set; } = new("http://localhost:5080/");
    public string ConnectionString { get; set; } = "Server=localhost;Database=BrokerSamples;Integrated Security=true;TrustServerCertificate=true";
    public string TopicName { get; set; } = "payments";
    /// <summary>Payments whose ID starts with this fail in the subscribers; empty turns failures off.</summary>
    public string FailPrefix { get; set; } = PaymentHandler.DefaultFailPrefix;

    public SampleApp Publisher { get; set; } = new();
    public SampleApp Webhook { get; set; } = new();
    public SampleApp SignalR { get; set; } = new();
    public SampleApp Pull { get; set; } = new();

    /// <summary>Topic the Blazor samples use: BlazorPublisher sends, BlazorSubscriber shows toasts.</summary>
    public string NotificationsTopicName { get; set; } = "notifications";
    public SampleApp BlazorPublisher { get; set; } = new();
    public SampleApp BlazorSubscriber { get; set; } = new();

    /// <summary>Console samples on the notifications topic: one publisher, and one subscription per delivery mode that ConsoleSubscriber listens on together.</summary>
    public SampleApp ConsolePublisher { get; set; } = new();
    public SampleApp ConsoleSignalR { get; set; } = new();
    public SampleApp ConsolePull { get; set; } = new();
    /// <summary>Url is where ConsoleSubscriber listens for webhook calls.</summary>
    public SampleApp ConsoleWebhook { get; set; } = new() { Url = "http://localhost:5084" };
}

public sealed class SampleApp
{
    public string ApiKey { get; set; } = "";
    public Guid SubscriptionId { get; set; }
    /// <summary>Webhook only: the signing secret(s); during rotation, the new one and the old one.</summary>
    public string[] Secrets { get; set; } = [];
    /// <summary>Webhook only: the address the subscriber listens on.</summary>
    public string Url { get; set; } = "http://localhost:5081";
}

public static class SampleConfiguration
{
    /// <summary>Adds samples/samples.local.json, found by walking up from the working directory.</summary>
    public static IConfigurationBuilder AddSampleSettings(this IConfigurationBuilder configuration)
    {
        var path = FindLocalFile();
        return path is null ? configuration : configuration.AddJsonFile(path, optional: true, reloadOnChange: false);
    }

    public static SampleSettings GetSampleSettings(this IConfiguration configuration) =>
        configuration.GetSection(SampleSettings.SectionName).Get<SampleSettings>() ?? new SampleSettings();

    /// <summary>samples/samples.local.json in the nearest directory that has a samples folder.</summary>
    public static string? FindLocalFile()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "samples", "Samples.Shared")))
                    return Path.Combine(dir.FullName, "samples", SampleSettings.LocalFileName);
        return null;
    }
}
