using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ConsolePublisher;
using ConsoleSubscriber;
using MessageBroker.Contracts.Client;
using MessageBroker.Contracts.Models;
using MessageBroker.Domain;
using MessageBroker.IntegrationTests.Infrastructure;
using MessageBroker.Worker.Push;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using SamplePublisher;
using Samples.Shared;

namespace MessageBroker.IntegrationTests.Samples;

/// <summary>S10 — The console samples' path: ConsolePublisher sends, ConsoleSubscriber gets it on all three channels.</summary>
public sealed class ConsoleSampleTests(SqlServerFixture sql) : ApiTest(sql)
{
    [Fact(DisplayName = "S10 Console samples: setup-console onboards only the console apps; a notification reaches ConsoleSubscriber over SignalR, Pull and Webhook; an invalid one is dead-lettered on each")]
    public async Task S10_NotificationReachesEveryChannel()
    {
        // setup-console on a file that already has the other samples: only the Console* entries change.
        var settings = new SampleSettings
        {
            BrokerUrl = Api.Server.BaseAddress,
            Publisher = new SampleApp { ApiKey = "kept" },
            ConsoleWebhook = { Url = $"https://{ApiFactory.SeededHost}" },
        };
        await SampleSetup.RunConsoleAsync(Admin, settings);
        Assert.Equal("kept", settings.Publisher.ApiKey);
        Assert.Empty(settings.BlazorSubscriber.ApiKey);
        Assert.Equal($"https://{ApiFactory.SeededHost}", settings.ConsoleWebhook.Url);
        Assert.Single(settings.ConsoleWebhook.Secrets);
        Assert.Equal($"https://{ApiFactory.SeededHost}{NotificationWebhook.Path}",
            await ScalarAsync<string>("SELECT WebhookUrl FROM broker.Subscriptions WHERE SubscriptionId = @id", new { id = settings.ConsoleWebhook.SubscriptionId }));

        // The test broker requires HTTPS (Development allows HTTP); point the subscription at a local HTTP listener.
        var webhookUrl = $"http://127.0.0.1:{FreePort()}";
        settings.ConsoleWebhook.Url = webhookUrl;
        await ExecAsync("UPDATE broker.Subscriptions SET WebhookUrl = @url WHERE SubscriptionId = @id",
            new { url = webhookUrl + NotificationWebhook.Path, id = settings.ConsoleWebhook.SubscriptionId });
        var modes = await QueryAsync<string>(
            "SELECT s.DeliveryMode FROM broker.Subscriptions s JOIN broker.Topics t ON t.TopicId = s.TopicId WHERE t.Name = @name ORDER BY s.DeliveryMode",
            new { name = settings.NotificationsTopicName });
        Assert.Equal(["Pull", "SignalR", "Webhook"], modes);

        // ConsoleSubscriber, as Program.Main builds it, reaching the in-process broker.
        var output = new StringWriter();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(webhookUrl);
        builder.Logging.ClearProviders();
        builder.Services.AddConsoleSubscriber(settings, () => Api.Server.CreateHandler(), UseTestServer);
        await using var app = builder.Build();
        app.MapNotificationWebhook();
        new NotificationPrinter(output, TimeProvider.System).Attach(app.Services);
        await app.StartAsync();

        var registry = Api.Services.GetRequiredService<MessageBroker.Application.Dispatch.IConnectionRegistry>();
        await WaitUntilAsync(() => Task.FromResult(registry.Count(settings.ConsoleSignalR.SubscriptionId) == 1));

        // What ConsolePublisher does for "Deploy done | v1.2 is live | Success".
        var sender = new NotificationSender(BrokerClient.Create(Api.CreateClient(), settings.ConsolePublisher.ApiKey), settings.NotificationsTopicName);
        var sent = Assert.IsType<UserNotification>(NotificationInput.Parse("Deploy done | v1.2 is live | Success", "test").Notification);
        var valid = await sender.SendAsync(sent);
        Assert.True(valid.Published, valid.Description);
        Assert.Equal(3, valid.DeliveryCount);
        var invalid = await BrokerClient.Create(Api.CreateClient(), settings.ConsolePublisher.ApiKey).PublishAsync(settings.NotificationsTopicName,
            new PublishRequest { MessageType = NotificationMessages.MessageType, Payload = JsonSerializer.SerializeToElement(new { text = "no title" }) });

        // SignalR and Webhook are pushed by the lease loop; the pull worker receives its own.
        Assert.Equal(4, await Api.Services.GetRequiredService<LeaseLoop>().RunOnceAsync(CancellationToken.None));
        await WaitUntilAsync(async () =>
            (await GetDeliveriesForMessageAsync(valid.MessageId)).All(d => d.State == DeliveryStatus.Completed)
            && (await GetDeliveriesForMessageAsync(invalid.MessageId)).All(d => d.State == DeliveryStatus.DeadLettered));
        await app.StopAsync();

        foreach (var channel in ConsoleChannels.All)
        {
            var received = Assert.Single(app.Services.GetRequiredKeyedService<NotificationFeed>(channel.Name).Items);
            Assert.Equal((valid.MessageId, sent), (received.MessageId, received.Notification));
        }
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["[Pull]", "[SignalR]", "[Webhook]"], lines.Select(l => l.Split(' ')[1]).Order());
        Assert.All(lines, l => Assert.Contains("SUCCESS Deploy done: v1.2 is live (from test", l));
        foreach (var deadLettered in await GetDeliveriesForMessageAsync(invalid.MessageId))
            Assert.Equal("RejectedBySubscriber", Assert.Single(await GetDeadLettersAsync(deadLettered.DeliveryId)).Reason);
    }

    [Fact(DisplayName = "S10 ConsolePublisher reports a refused publish instead of throwing")]
    public async Task S10_RefusedPublishIsReported()
    {
        var sender = new NotificationSender(BrokerClient.Create(Api.CreateClient(), "mbk_not_a_key"), "notifications");

        var outcome = await sender.SendAsync(new UserNotification("t", "", NotificationLevel.Info, "test"));

        Assert.False(outcome.Published);
        Assert.StartsWith("The broker refused it: 401", outcome.Description);
    }

    private void UseTestServer(HttpConnectionOptions o)
    {
        o.Transports = HttpTransportType.LongPolling;
        o.HttpMessageHandlerFactory = _ => Api.Server.CreateHandler();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Condition was not met in time.");
            await Task.Delay(50);
        }
    }
}

/// <summary>S11 — ConsolePublisher's input line and ConsoleSubscriber's output line, without a broker.</summary>
public class ConsoleSampleFormatTests
{
    [Theory(DisplayName = "S11a A typed line becomes a notification: Title | message | level")]
    [InlineData("Hello", "Hello", "", NotificationLevel.Info)]
    [InlineData("  Hello  |  there ", "Hello", "there", NotificationLevel.Info)]
    [InlineData("Deploy | v1.2 is live | success", "Deploy", "v1.2 is live", NotificationLevel.Success)]
    [InlineData("Disk | 90% | full | WARNING", "Disk", "90% | full", NotificationLevel.Warning)]
    [InlineData("Oops | | Error", "Oops", "", NotificationLevel.Error)]
    public void Parses_line(string line, string title, string text, NotificationLevel level)
    {
        var result = NotificationInput.Parse(line, "me");

        Assert.Null(result.Error);
        Assert.Equal(new UserNotification(title, text, level, "me"), result.Notification);
    }

    [Theory(DisplayName = "S11b A line without a title, with an unknown level or too long is refused with a reason")]
    [InlineData("", "title")]
    [InlineData(" | text", "title")]
    [InlineData("a | b | Loud", "Unknown level 'Loud'")]
    [InlineData("a | b | 1", "Unknown level '1'")]
    public void Refuses_line(string line, string reason)
    {
        var result = NotificationInput.Parse(line, "me");

        Assert.Null(result.Notification);
        Assert.Contains(reason, result.Error);
    }

    [Fact(DisplayName = "S11b Title and message lengths are capped")]
    public void Refuses_long_parts()
    {
        Assert.Null(NotificationInput.Parse(new string('t', NotificationInput.MaxTitleLength + 1), "me").Notification);
        Assert.NotNull(NotificationInput.Parse(new string('t', NotificationInput.MaxTitleLength), "me").Notification);
        Assert.Null(NotificationInput.Parse("t | " + new string('x', NotificationInput.MaxTextLength + 1), "me").Notification);
    }

    [Fact(DisplayName = "S11c A received notification prints as one line with its channel, level and latency")]
    public void Formats_line()
    {
        var time = new FakeTimeProvider();
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var printer = new NotificationPrinter(TextWriter.Null, time);
        var published = new DateTime(2026, 10, 8, 12, 3, 4, DateTimeKind.Utc);
        ReceivedNotification Item(string text, DateTime received) =>
            new(Guid.NewGuid(), new UserNotification("Deploy done", text, NotificationLevel.Success, "ConsolePublisher"), published, received);

        Assert.Equal("12:03:04 [Pull] SUCCESS Deploy done: v1.2 is live (from ConsolePublisher, 35 ms after publish)",
            printer.Format("Pull", Item("v1.2 is live", published.AddMilliseconds(35))));
        // No message, and a receiver clock behind the broker's: no latency.
        Assert.Equal("12:03:03 [Webhook] SUCCESS Deploy done (from ConsolePublisher)",
            printer.Format("Webhook", Item("", published.AddSeconds(-1))));
    }
}
