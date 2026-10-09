using System.Collections.Concurrent;
using System.Net;
using System.Text;
using MessageBroker.Contracts;
using MessageBroker.Contracts.Client;
using MessageBroker.Contracts.Models;
using MessageBroker.Contracts.Webhooks;
using MessageBroker.Domain;
using MessageBroker.IntegrationTests.Infrastructure;
using MessageBroker.IntegrationTests.PushChannels;
using MessageBroker.Worker.Maintenance;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Samples.Shared;
using WebhookSubscriber;

namespace MessageBroker.IntegrationTests.Samples;

public sealed class SampleSubscriberTests(SqlServerFixture sql) : WebhookTest(sql)
{
    private sealed record Invoice(string Subscriber, string PaymentId, decimal Amount, string Status, int TimesApplied)
    {
        public static Invoice Read(SqlDataReader r) => new(
            Col<string>(r, 0), Col<string>(r, 1), Col<decimal>(r, 2), Col<string>(r, 3), Col<int>(r, 4));
    }

    private SampleDatabase SampleDb => new(Sql.ConnectionString);

    private PaymentHandler Handler(string subscriber) => new(new InvoiceStore(SampleDb), subscriber, NullLogger.Instance);

    private Task<IReadOnlyList<Invoice>> InvoicesAsync() =>
        QueryAsync<Invoice>("SELECT Subscriber, PaymentId, Amount, Status, TimesApplied FROM sample.Invoices ORDER BY PaymentId", Invoice.Read);

    private Task<int> ProcessedAsync(string subscriber) =>
        ScalarAsync<int>("SELECT COUNT(*) FROM sample.ProcessedMessages WHERE Subscriber = @subscriber", P("subscriber", subscriber));

    private async Task<(TopicResponse Topic, HttpClient Publisher, Guid OwnerId, string OwnerKey)> ArrangeAsync()
    {
        var topic = await CreateTopicViaApiAsync();
        var (publisherId, _, publisher) = await CreateAppClientAsync();
        await GrantViaApiAsync(publisherId, "Topic", topic.TopicId, "Publish");
        var (ownerId, ownerKey, _) = await CreateAppClientAsync();
        return (topic, publisher, ownerId, ownerKey);
    }

    private static async Task<Guid> PublishPaymentAsync(HttpClient publisher, string topic, string paymentId, decimal amount) =>
        (await ReadAsync<PublishResponse>(await PublishViaApiAsync(publisher, topic, new
        {
            messageType = "PaymentProcessed.v1",
            correlationId = paymentId,
            payload = new { paymentId, amount, currency = "INR" },
        }), HttpStatusCode.Created)).MessageId;

    private async Task<DeliveryRow> DeliveryOfAsync(Guid messageId) => Assert.Single(await GetDeliveriesForMessageAsync(messageId));

    /// <summary>Lets a lapsed lease go through maintenance and skips the retry backoff.</summary>
    private async Task ExpireLeaseAsync(long deliveryId)
    {
        await LapseLeaseAsync(deliveryId);
        await Api.Services.GetRequiredService<MaintenanceLoop>().RunOnceAsync(CancellationToken.None);
        await MakeDueAsync(deliveryId);
    }

    [Fact(DisplayName = "S02 Pull sample: a redelivered message is skipped by ProcessedMessages and still ACKed; failures retry or dead-letter")]
    public async Task S02_PullSubscriber()
    {
        var s = await ArrangeAsync();
        var subscription = await CreateSubscriptionAsync(s.Topic.TopicId, s.OwnerId, mode: "Pull");
        var settings = new SampleSettings { Pull = new SampleApp { ApiKey = s.OwnerKey, SubscriptionId = subscription.SubscriptionId } };
        var broker = BrokerClient.Create(Api.CreateClient(), s.OwnerKey);
        var handler = Handler("pull");
        var worker = new PullPaymentWorker(settings, broker, handler, NullLogger<PullPaymentWorker>.Instance);

        // First attempt: the invoice change commits, then the subscriber dies before its ACK.
        await PublishPaymentAsync(s.Publisher, s.Topic.Name, "PAY-S02", 250m);
        var first = Assert.Single(await broker.ReceiveAsync(subscription.SubscriptionId));
        Assert.True((await handler.HandleAsync(first, CancellationToken.None)).Success);
        await ExpireLeaseAsync(first.DeliveryId);

        // The redelivery is recognised, not applied again, and ACKed.
        Assert.Equal(1, await worker.RunOnceAsync(waitSeconds: 0, CancellationToken.None));
        Assert.Equal(DeliveryStatus.Completed, (await GetDeliveryAsync(first.DeliveryId)).State);
        Assert.Equal(["LeaseExpired", "Acked"], (await GetAttemptsAsync(first.DeliveryId)).Select(a => a.Outcome));
        Assert.Equal(new Invoice("pull", "PAY-S02", 250m, "Paid", 1), Assert.Single(await InvoicesAsync()));
        Assert.Equal(1, await ProcessedAsync("pull"));

        // Failure paths: a downstream failure is NACKed for retry; an invalid payment goes straight to the DLQ.
        var failing = await DeliveryOfAsync(await PublishPaymentAsync(s.Publisher, s.Topic.Name, "FAIL-S02", 10m));
        var invalid = await DeliveryOfAsync(await PublishPaymentAsync(s.Publisher, s.Topic.Name, "PAY-S02-ZERO", 0m));
        Assert.Equal(2, await worker.RunOnceAsync(waitSeconds: 0, CancellationToken.None));

        failing = await GetDeliveryAsync(failing.DeliveryId);
        Assert.Equal((DeliveryStatus.Pending, 1), (failing.State, failing.AttemptCount));
        Assert.Equal("DownstreamUnavailable", Assert.Single(await GetAttemptsAsync(failing.DeliveryId)).ErrorCode);
        Assert.Equal("RejectedBySubscriber", Assert.Single(await GetDeadLettersAsync(invalid.DeliveryId)).Reason);
        Assert.Single(await InvoicesAsync());
    }

    [Fact(DisplayName = "S02 Webhook sample: verifies signatures, answers 200 once processed (and for a repeat), 503 to retry, REST NACK to dead-letter")]
    public async Task S02_WebhookSubscriber()
    {
        var s = await ArrangeAsync();

        // Host the sample's endpoint on a real port; its broker client reaches the in-process broker.
        var settings = new SampleSettings { ConnectionString = Sql.ConnectionString, Webhook = new SampleApp { ApiKey = s.OwnerKey, Secrets = [Secret] } };
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddPaymentSubscriber(settings, settings.Webhook, subscriber: "webhook")
            .ConfigureHttpClient(c => c.BaseAddress = Api.Server.BaseAddress)
            .ConfigurePrimaryHttpMessageHandler(() => Api.Server.CreateHandler());
        await using var app = builder.Build();
        app.MapPaymentWebhook();
        await app.StartAsync();
        var url = app.Urls.Single() + PaymentWebhook.Path;

        var subscription = await CreateSubscriptionAsync(s.Topic.TopicId, s.OwnerId, mode: "Webhook", webhookUrl: url,
            protectedSecret: Protect(Secret), maxAttempts: 3);

        var paid = await PublishPaymentAsync(s.Publisher, s.Topic.Name, "PAY-W1", 99.5m);
        var failing = await PublishPaymentAsync(s.Publisher, s.Topic.Name, "FAIL-W2", 5m);
        var invalid = await PublishPaymentAsync(s.Publisher, s.Topic.Name, "PAY-W3", 0m);
        Assert.Equal(3, await LeasePassAsync());

        var paidDelivery = await DeliveryOfAsync(paid);
        Assert.Equal(DeliveryStatus.Completed, paidDelivery.State);
        Assert.Equal(200, Assert.Single(await GetAttemptsAsync(paidDelivery.DeliveryId)).HttpStatusCode);

        var failingDelivery = await DeliveryOfAsync(failing);
        Assert.Equal(DeliveryStatus.Pending, failingDelivery.State);
        Assert.Equal("Http503", Assert.Single(await GetAttemptsAsync(failingDelivery.DeliveryId)).ErrorCode);

        // 202, with the REST NACK already recorded: dead-lettered on the first attempt.
        var invalidDelivery = await DeliveryOfAsync(invalid);
        Assert.Equal(DeliveryStatus.DeadLettered, invalidDelivery.State);
        var deadLetter = Assert.Single(await GetDeadLettersAsync(invalidDelivery.DeliveryId));
        Assert.Equal(("RejectedBySubscriber", 1), (deadLetter.Reason, deadLetter.AttemptCount));
        Assert.Equal("InvalidPayload", Assert.Single(await GetAttemptsAsync(invalidDelivery.DeliveryId)).ErrorCode);

        // A repeat of the processed delivery (as after a lost 200) is answered 200 and not applied again.
        var message = await new BrokerClient(Admin).GetMessageAsync(paid);
        var body = System.Text.Json.JsonSerializer.Serialize(new Delivery
        {
            DeliveryId = paidDelivery.DeliveryId,
            LockToken = Guid.NewGuid(),
            LockedUntil = DateTime.UtcNow.AddMinutes(1),
            Attempt = 2,
            Message = new DeliveredMessage
            {
                MessageId = paid, MessageType = message.MessageType, CorrelationId = message.CorrelationId,
                CreatedAt = message.CreatedAt, Payload = message.Payload,
            },
        }, System.Text.Json.JsonSerializerOptions.Web);
        using var http = new HttpClient();
        HttpRequestMessage Signed(string secret)
        {
            var timestamp = WebhookSignature.Timestamp(DateTimeOffset.UtcNow);
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add(WebhookHeaders.Timestamp, timestamp);
            request.Headers.Add(WebhookHeaders.Signature, WebhookSignature.CreateHeader(timestamp, body, secret));
            return request;
        }
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Signed(Secret))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Signed("not-the-secret"))).StatusCode);

        Assert.Equal(new Invoice("webhook", "PAY-W1", 99.5m, "Paid", 1), Assert.Single(await InvoicesAsync()));
        Assert.Equal(1, await ProcessedAsync("webhook"));
        Assert.Equal(subscription.SubscriptionId, paidDelivery.SubscriptionId);
    }

    [Fact(DisplayName = "S02 SignalR sample: after a disconnect, the redelivered message is skipped by ProcessedMessages and still ACKed")]
    public async Task S02_SignalRSubscriber()
    {
        var s = await ArrangeAsync();
        var subscription = await CreateSubscriptionAsync(s.Topic.TopicId, s.OwnerId, mode: "SignalR", lockSeconds: 30);
        void UseTestServer(Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions o)
        {
            o.Transports = HttpTransportType.LongPolling;
            o.HttpMessageHandlerFactory = _ => Api.Server.CreateHandler();
        }

        // A first client processes the payment, then drops before it can ACK.
        var handler = Handler("signalr");
        var received = new ConcurrentQueue<Delivery>();
        var crashing = new HubConnectionBuilder().WithUrl(new Uri(Api.Server.BaseAddress, DeliveryHub.Path), o =>
        {
            UseTestServer(o);
            o.AccessTokenProvider = () => Task.FromResult<string?>(s.OwnerKey);
        }).Build();
        crashing.On<Delivery>(DeliveryHub.Deliver, async d =>
        {
            await handler.HandleAsync(d, CancellationToken.None);
            received.Enqueue(d);
        });
        await crashing.StartAsync();
        await crashing.InvokeAsync(DeliveryHub.Subscribe, subscription.SubscriptionId);

        await PublishPaymentAsync(s.Publisher, s.Topic.Name, "PAY-R1", 42m);
        // Don't wait for the delivery to settle: it never will, and the pass would sit out the lease.
        var leaseLoop = Api.Services.GetRequiredService<MessageBroker.Worker.Push.LeaseLoop>();
        Assert.Equal(1, await leaseLoop.RunOnceAsync(CancellationToken.None, waitForDeliveries: false));
        await WaitUntilAsync(() => Task.FromResult(!received.IsEmpty));
        var first = Assert.Single(received);
        await crashing.DisposeAsync();
        await ExpireLeaseAsync(first.DeliveryId);

        // The sample worker joins and gets the redelivery.
        var settings = new SampleSettings { BrokerUrl = Api.Server.BaseAddress, SignalR = new SampleApp { ApiKey = s.OwnerKey, SubscriptionId = subscription.SubscriptionId } };
        using var worker = new SignalRPaymentWorker(settings, handler, NullLogger<SignalRPaymentWorker>.Instance, UseTestServer);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var registry = Api.Services.GetRequiredService<MessageBroker.Application.Dispatch.IConnectionRegistry>();
            await WaitUntilAsync(() => Task.FromResult(registry.Count(subscription.SubscriptionId) == 1));
            Assert.Equal(1, await LeasePassAsync());
            await WaitUntilAsync(async () => (await GetDeliveryAsync(first.DeliveryId)).State == DeliveryStatus.Completed);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(["LeaseExpired", "Acked"], (await GetAttemptsAsync(first.DeliveryId)).Select(a => a.Outcome));
        Assert.Equal(new Invoice("signalr", "PAY-R1", 42m, "Paid", 1), Assert.Single(await InvoicesAsync()));
        Assert.Equal(1, await ProcessedAsync("signalr"));
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
