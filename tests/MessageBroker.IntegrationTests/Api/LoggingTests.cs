using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MessageBroker.Api.Hosting;
using MessageBroker.Application.Security;
using MessageBroker.Contracts;
using MessageBroker.Contracts.Client;
using MessageBroker.Contracts.Models;
using MessageBroker.IntegrationTests.Infrastructure;
using MessageBroker.Worker.Push;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MessageBroker.IntegrationTests.Api;

public sealed class LoggingTests(SqlServerFixture sql) : ApiTest(sql)
{
    [Fact(DisplayName = "A11 Logs carry MessageId, CorrelationId and AppId scopes; access_token is redacted; payloads and keys never appear")]
    public async Task A11_StructuredLogs()
    {
        var capture = new CapturingLoggerProvider();
        // Every category at Trace, including an attempt to turn the framework request lines back on.
        await using var api = Api.WithWebHostBuilder(b =>
        {
            b.UseSetting("Logging:LogLevel:Default", "Trace");
            b.UseSetting("Logging:LogLevel:Microsoft", "Trace");
            b.UseSetting("Logging:LogLevel:Microsoft.AspNetCore", "Trace");
            b.UseSetting("Logging:LogLevel:" + RequestLogging.HostingCategory, "Trace");
            b.UseSetting("Logging:LogLevel:" + RequestLogging.ActionInvokerCategory, "Trace");
            b.ConfigureLogging(l => l.AddProvider(capture));
        });
        HttpClient Client(string key)
        {
            var client = api.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(ApiKeys.Scheme, key);
            return client;
        }

        var topic = await CreateTopicViaApiAsync();
        var (publisherId, publisherKey, _) = await CreateAppClientAsync();
        await GrantViaApiAsync(publisherId, "Topic", topic.TopicId, "Publish");
        var (subscriberId, subscriberKey, _) = await CreateAppClientAsync();
        var pull = await CreateSubscriptionAsync(topic.TopicId, subscriberId, mode: "Pull");
        var signalR = await CreateSubscriptionAsync(topic.TopicId, subscriberId, mode: "SignalR");
        var marker = $"card-4111111111111111-{Guid.NewGuid():N}";

        // Publish, pull and ACK.
        var published = await ReadAsync<PublishResponse>(await PublishViaApiAsync(Client(publisherKey), topic.Name, new
        {
            messageType = "PaymentProcessed.v1",
            correlationId = "CORR-A11",
            properties = new { note = marker },
            payload = new { paymentId = "PAY-A11", cardNumber = marker },
        }, idempotencyKey: "outbox-a11"), HttpStatusCode.Created);
        var subscriber = Client(subscriberKey);
        var delivery = Assert.Single(await ReadAsync<List<Delivery>>(await subscriber.PostAsJsonAsync(
            $"/api/v1/subscriptions/{pull.SubscriptionId}/receive", new ReceiveRequest())));
        await ExpectAsync(subscriber.PostAsJsonAsync($"/api/v1/deliveries/{delivery.DeliveryId}/ack", new AckRequest(delivery.LockToken)),
            HttpStatusCode.NoContent);

        // A browser-style hub negotiate with the key in the query string, and one with a wrong key.
        var negotiate = await api.CreateClient().PostAsync(
            $"{DeliveryHub.Path}/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(subscriberKey)}", null);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
        var wrongKey = ApiKeys.Generate().Key;
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().PostAsync(
            $"{DeliveryHub.Path}/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(wrongKey)}", null)).StatusCode);

        // A SignalR delivery, so the hub and transport internals log at Trace with the payload in flight.
        var handled = new ConcurrentQueue<Delivery>();
        await using (var listener = new SignalRDeliveryListener(api.Server.BaseAddress, subscriberKey,
            (d, _) => { handled.Enqueue(d); return Task.FromResult(DeliveryResult.Ack); },
            o => { o.Transports = HttpTransportType.LongPolling; o.HttpMessageHandlerFactory = _ => api.Server.CreateHandler(); }))
        {
            await listener.StartAsync([signalR.SubscriptionId]);
            Assert.Equal(1, await api.Services.GetRequiredService<LeaseLoop>().RunOnceAsync(CancellationToken.None, true)
                .WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Single(handled);
        }

        var logs = capture.Logs;

        // The publish event carries the message, correlation and caller IDs.
        var publishLog = Assert.Single(logs, l => l.Message.Contains("published to") && l.Scopes.GetValueOrDefault("MessageId") == published.MessageId.ToString());
        Assert.Equal(("CORR-A11", publisherId.ToString()), (publishLog.Scopes["CorrelationId"], publishLog.Scopes["AppId"]));

        // The pull lease event carries the delivery's IDs, and the caller's AppId comes from the request scope.
        var leaseLog = Assert.Single(logs, l => l.Message.Contains("leased to") && l.Scopes.GetValueOrDefault("DeliveryId") == delivery.DeliveryId.ToString());
        Assert.Equal((published.MessageId.ToString(), "CORR-A11", pull.SubscriptionId.ToString(), subscriberId.ToString()),
            (leaseLog.Scopes["MessageId"], leaseLog.Scopes["CorrelationId"], leaseLog.Scopes["SubscriptionId"], leaseLog.Scopes["AppId"]));
        var ackLog = Assert.Single(logs, l => l.Message.Contains($"Delivery {delivery.DeliveryId} acknowledged"));
        Assert.Equal(subscriberId.ToString(), ackLog.Scopes["AppId"]);

        // The SignalR send event carries the same IDs.
        var sendLog = Assert.Single(logs, l => l.Message.Contains("sent over SignalR"));
        Assert.Equal((published.MessageId.ToString(), "CORR-A11", signalR.SubscriptionId.ToString()),
            (sendLog.Scopes["MessageId"], sendLog.Scopes["CorrelationId"], sendLog.Scopes["SubscriptionId"]));

        // One request line per call, with the token redacted; the framework's own request lines stay off.
        var negotiateLines = logs.Where(l => l.Category == "MessageBroker.Api.Requests" && l.Message.Contains("access_token")).ToList();
        Assert.Equal(2, negotiateLines.Count);
        Assert.All(negotiateLines, l => Assert.Contains($"{DeliveryHub.Path}/negotiate?negotiateVersion=1&access_token=***", l.Message));
        Assert.Contains(negotiateLines, l => l.Message.Contains("responded 200") && l.Scopes.GetValueOrDefault("AppId") == subscriberId.ToString());
        Assert.Contains(negotiateLines, l => l.Message.Contains("responded 401"));
        Assert.DoesNotContain(logs, l => l.Category == RequestLogging.HostingCategory && l.Level < LogLevel.Warning);

        // Nothing secret or private anywhere: not the payload, not the properties, not a key.
        foreach (var secret in new[] { marker, publisherKey, subscriberKey, wrongKey, ApiFactory.AdminKey })
            Assert.DoesNotContain(logs, l => l.AllText.Contains(secret, StringComparison.Ordinal));
        Assert.True(logs.Count > 50, $"only {logs.Count} events were captured");
    }
}
