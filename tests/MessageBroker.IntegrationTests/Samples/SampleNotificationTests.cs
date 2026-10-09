using System.Collections.Concurrent;
using System.Text.Json;
using MessageBroker.Contracts.Client;
using MessageBroker.Contracts.Models;
using MessageBroker.Domain;
using MessageBroker.IntegrationTests.Infrastructure;
using MessageBroker.Worker.Push;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SamplePublisher;
using Samples.Shared;

namespace MessageBroker.IntegrationTests.Samples;

/// <summary>S04 — The Blazor samples' path: publish a notification, the subscriber's feed raises the toast.</summary>
public sealed class SampleNotificationTests(SqlServerFixture sql) : ApiTest(sql)
{
    [Fact(DisplayName = "S04 Blazor samples: a published notification reaches the subscriber's feed over SignalR; an invalid one is dead-lettered")]
    public async Task S04_NotificationReachesFeed()
    {
        var settings = await SampleSetup.RunAsync(Admin,
            new SampleSettings { BrokerUrl = Api.Server.BaseAddress, Webhook = { Url = $"https://{ApiFactory.SeededHost}" }, ConsoleWebhook = { Url = $"https://{ApiFactory.SeededHost}" } });
        // Setup also onboards the console samples on this topic (S10 covers them); without their listeners
        // they would only add deliveries that never settle.
        foreach (var console in new[] { settings.ConsoleSignalR, settings.ConsolePull, settings.ConsoleWebhook })
            (await Admin.DeleteAsync($"api/v1/subscriptions/{console.SubscriptionId}")).EnsureSuccessStatusCode();

        var feed = new NotificationFeed();
        var toasts = new ConcurrentQueue<ReceivedNotification>();
        feed.Received += toasts.Enqueue;
        var handler = new NotificationHandler(feed, TimeProvider.System, NullLogger<NotificationHandler>.Instance);
        using var worker = new SignalRSubscriberWorker(settings.BrokerUrl, settings.BlazorSubscriber, handler.HandleAsync,
            NullLogger.Instance, UseTestServer);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var registry = Api.Services.GetRequiredService<MessageBroker.Application.Dispatch.IConnectionRegistry>();
            await WaitUntilAsync(() => Task.FromResult(registry.Count(settings.BlazorSubscriber.SubscriptionId) == 1));

            // What BlazorPublisher's Send button does.
            var publisher = BrokerClient.Create(Api.CreateClient(), settings.BlazorPublisher.ApiKey);
            var sent = new UserNotification("Build finished", "All 240 tests passed.", NotificationLevel.Success, "test");
            var valid = await publisher.PublishAsync(settings.NotificationsTopicName, NotificationMessages.ToPublishRequest(sent),
                Guid.NewGuid().ToString("N"));
            var invalid = await publisher.PublishAsync(settings.NotificationsTopicName, new PublishRequest
            {
                MessageType = NotificationMessages.MessageType,
                Payload = JsonSerializer.SerializeToElement(new { text = "no title" }),
            });
            Assert.Equal(1, valid.DeliveryCount);

            Assert.Equal(2, await Api.Services.GetRequiredService<LeaseLoop>().RunOnceAsync(CancellationToken.None));

            var validDelivery = Assert.Single(await GetDeliveriesForMessageAsync(valid.MessageId));
            var invalidDelivery = Assert.Single(await GetDeliveriesForMessageAsync(invalid.MessageId));
            await WaitUntilAsync(async () => (await GetDeliveryAsync(validDelivery.DeliveryId)).State == DeliveryStatus.Completed
                && (await GetDeliveryAsync(invalidDelivery.DeliveryId)).State == DeliveryStatus.DeadLettered);

            var toast = Assert.Single(toasts);
            Assert.Equal(valid.MessageId, toast.MessageId);
            Assert.Equal(sent, toast.Notification);
            Assert.Equal("RejectedBySubscriber", Assert.Single(await GetDeadLettersAsync(invalidDelivery.DeliveryId)).Reason);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private void UseTestServer(HttpConnectionOptions o)
    {
        o.Transports = HttpTransportType.LongPolling;
        o.HttpMessageHandlerFactory = _ => Api.Server.CreateHandler();
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

/// <summary>S05 — The Blazor subscriber's handler and feed, without a broker.</summary>
public class NotificationHandlerTests
{
    private readonly NotificationFeed _feed = new();
    private readonly List<ReceivedNotification> _toasts = [];
    private readonly NotificationHandler _handler;

    public NotificationHandlerTests()
    {
        _feed.Received += _toasts.Add;
        _handler = new NotificationHandler(_feed, TimeProvider.System, NullLogger<NotificationHandler>.Instance);
    }

    private static Delivery DeliveryOf(Guid messageId, string messageType, object payload) => new()
    {
        DeliveryId = 1,
        Attempt = 1,
        Message = new DeliveredMessage
        {
            MessageId = messageId,
            MessageType = messageType,
            CreatedAt = DateTime.UtcNow,
            Payload = JsonSerializer.SerializeToElement(payload, NotificationMessages.Json),
        },
    };

    [Fact(DisplayName = "S05a A redelivered notification is ACKed without a second toast")]
    public async Task Redelivery_is_acked_once_shown()
    {
        var delivery = DeliveryOf(Guid.NewGuid(), NotificationMessages.MessageType,
            new UserNotification("Hi", "There", NotificationLevel.Warning, "test"));

        Assert.True((await _handler.HandleAsync(delivery, CancellationToken.None)).Success);
        Assert.True((await _handler.HandleAsync(delivery, CancellationToken.None)).Success);

        Assert.Single(_toasts);
        Assert.Equal(NotificationLevel.Warning, Assert.Single(_feed.Items).Notification.Level);
    }

    [Theory(DisplayName = "S05b An unreadable notification is dead-lettered and shows no toast")]
    [InlineData("UserNotification", "{\"text\":\"no title\"}")]
    [InlineData("UserNotification", "{\"title\":\"x\",\"level\":\"Loud\"}")]
    [InlineData("UserNotification", "[1,2]")]
    [InlineData("PaymentReceived", "{\"title\":\"x\",\"text\":\"y\"}")]
    public async Task Invalid_payload_is_dead_lettered(string messageType, string json)
    {
        var result = await _handler.HandleAsync(DeliveryOf(Guid.NewGuid(), messageType, JsonDocument.Parse(json).RootElement), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.DeadLetter);
        Assert.Empty(_toasts);
    }

    [Fact(DisplayName = "S05c The feed keeps the newest 50, newest first")]
    public void Feed_keeps_newest()
    {
        var ids = Enumerable.Range(0, 55).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids)
            _feed.TryAdd(new ReceivedNotification(id, new UserNotification("t", "x", NotificationLevel.Info, "test"), DateTime.UtcNow, DateTime.UtcNow));

        Assert.Equal(50, _feed.Items.Count);
        Assert.Equal(ids[^1], _feed.Items[0].MessageId);
        Assert.Equal(ids[5], _feed.Items[^1].MessageId);
    }
}
