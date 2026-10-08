using MessageBroker.Contracts.Client;
using Microsoft.AspNetCore.Http.Connections.Client;
using Samples.Shared;

namespace ConsoleSubscriber;

public static class ConsoleSubscriberServices
{
    public const string BrokerHttpClient = "broker";

    /// <summary>
    /// Registers, per channel, a <see cref="NotificationFeed"/>, a <see cref="NotificationHandler"/> and a
    /// <see cref="BrokerClient"/> with that subscription's key (keyed by the channel name), plus the
    /// SignalR and pull workers; <see cref="NotificationWebhook"/> serves the webhook channel. Tests pass
    /// <paramref name="brokerHandler"/> and <paramref name="configureConnection"/> to reach an in-process broker.
    /// </summary>
    public static IServiceCollection AddConsoleSubscriber(this IServiceCollection services, SampleSettings settings,
        Func<HttpMessageHandler>? brokerHandler = null, Action<HttpConnectionOptions>? configureConnection = null)
    {
        services.AddSingleton(settings);
        services.AddSingleton(TimeProvider.System);
        var http = services.AddHttpClient(BrokerHttpClient, c => c.BaseAddress = settings.BrokerUrl);
        if (brokerHandler is not null)
            http.ConfigurePrimaryHttpMessageHandler(brokerHandler);

        foreach (var channel in ConsoleChannels.All)
        {
            var app = channel.App(settings);
            services.AddKeyedSingleton(channel.Name, (_, _) => new NotificationFeed());
            services.AddKeyedSingleton(channel.Name, (sp, key) => new NotificationHandler(
                sp.GetRequiredKeyedService<NotificationFeed>(key), sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<NotificationHandler>>()));
            services.AddKeyedSingleton(channel.Name, (sp, _) => BrokerClient.Create(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(BrokerHttpClient), app.ApiKey));
        }

        services.AddHostedService(sp => new SignalRSubscriberWorker(settings.BrokerUrl, settings.ConsoleSignalR,
            sp.GetRequiredKeyedService<NotificationHandler>(ConsoleChannels.SignalR.Name).HandleAsync,
            sp.GetRequiredService<ILogger<SignalRSubscriberWorker>>(), configureConnection));
        services.AddHostedService(sp => new PullSubscriberWorker(
            sp.GetRequiredKeyedService<BrokerClient>(ConsoleChannels.Pull.Name), settings.ConsolePull,
            sp.GetRequiredKeyedService<NotificationHandler>(ConsoleChannels.Pull.Name).HandleAsync,
            sp.GetRequiredService<ILogger<PullSubscriberWorker>>()));
        return services;
    }
}
