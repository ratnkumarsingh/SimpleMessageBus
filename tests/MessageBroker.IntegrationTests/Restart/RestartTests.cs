using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MessageBroker.Application.Dispatch;
using MessageBroker.Application.Security;
using MessageBroker.Contracts.Client;
using MessageBroker.Contracts.Models;
using MessageBroker.Domain;
using MessageBroker.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace MessageBroker.IntegrationTests.Restart;

/// <summary>
/// R01, R02 — the broker process stops and a new one starts on the same database, with its background
/// loops running as in production. Both instances share a Data Protection key directory, as a real
/// deployment must, so the new instance can read the webhook secrets the old one protected.
/// </summary>
public sealed class RestartTests(SqlServerFixture sql, ITestOutputHelper output) : ApiTest(sql)
{
    private const int LockSeconds = 3, MaintenanceSeconds = 1, RetrySeconds = 1;

    private readonly string _keysDirectory = Directory.CreateTempSubdirectory("broker-keys-").FullName;
    private volatile ApiFactory? _broker;
    // Captured once per instance. Calling CreateClient or Server on a factory that is being disposed
    // starts a new host, which would keep a dispatcher running into later tests.
    private volatile TestServer? _server;

    private ApiFactory Broker => _broker ?? throw new InvalidOperationException("The broker is down.");

    private ApiFactory StartBroker()
    {
        var broker = new ApiFactory(Sql.ConnectionString, new Dictionary<string, string?>
        {
            ["Broker:Dispatcher:Enabled"] = "true",
            ["Broker:Dispatcher:PollIntervalMs"] = "100",
            ["Broker:Dispatcher:MaintenanceIntervalSeconds"] = MaintenanceSeconds.ToString(),
            ["Broker:DataProtection:KeysDirectory"] = _keysDirectory,
        });
        _ = broker.Services; // starts the host and its loops
        _server = broker.Server;
        _broker = broker;
        return broker;
    }

    private async Task StopBrokerAsync()
    {
        var broker = Broker;
        _server = null;
        _broker = null;
        await broker.DisposeAsync();
    }

    public override async Task DisposeAsync()
    {
        if (_broker is not null)
            await StopBrokerAsync();
        await base.DisposeAsync();
        try
        {
            Directory.Delete(_keysDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact(DisplayName = "R01 Broker stopped mid-load and restarted: every accepted message completes; orphaned leases recover within lock + maintenance interval")]
    public async Task R01_RestartMidLoad()
    {
        await using var endpoint = await FakeWebhookEndpoint.StartAsync();
        endpoint.Respond("/hook", 200, delay: TimeSpan.FromMilliseconds(150));
        var topic = await CreateTopicViaApiAsync();
        var (publisherId, publisherKey, _) = await CreateAppClientAsync();
        await GrantViaApiAsync(publisherId, "Topic", topic.TopicId, "Publish");
        var (subscriberId, _, _) = await CreateAppClientAsync();

        StartBroker();
        var subscription = await CreateSubscriptionAsync(topic.TopicId, subscriberId, mode: "Webhook", maxAttempts: 20,
            lockSeconds: LockSeconds, retryBase: RetrySeconds, retryMax: RetrySeconds, maxConcurrent: 8, webhookTimeout: 2,
            webhookUrl: endpoint.Url + "/hook", protectedSecret: Broker.Services.GetRequiredService<ISecretProtector>().Protect("whsec-r01-0123456789"));

        // One publisher that keeps going through the outage, retrying each message with its idempotency key.
        var accepted = new ConcurrentDictionary<Guid, string>();
        var failedCalls = 0;
        using var publishing = new CancellationTokenSource();
        var publisher = Task.Run(async () =>
        {
            for (var n = 0; !publishing.IsCancellationRequested; n++)
            {
                var key = $"r01-{n}";
                while (!publishing.IsCancellationRequested)
                {
                    try
                    {
                        using var client = (_server ?? throw new InvalidOperationException("The broker is down.")).CreateClient();
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(ApiKeys.Scheme, publisherKey);
                        var response = await PublishViaApiAsync(client, topic.Name, idempotencyKey: key);
                        if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
                        {
                            accepted[(await response.Content.ReadFromJsonAsync<PublishResponse>())!.MessageId] = key;
                            break;
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or HttpRequestException or OperationCanceledException)
                    {
                    }
                    Interlocked.Increment(ref failedCalls);
                    await Task.Delay(100);
                }
                await Task.Delay(10);
            }
        });

        await Task.Delay(2_000);
        await StopBrokerAsync();
        var orphaned = await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Deliveries WHERE Status = 1");
        var backlog = await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Deliveries WHERE Status = 0");
        await Task.Delay(1_000);
        StartBroker();
        var restartedAt = await DbNowAsync();
        await Task.Delay(1_000);
        await publishing.CancelAsync();
        await publisher;

        var clock = Stopwatch.StartNew();
        while (await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Deliveries WHERE Status <> 2") > 0)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "Deliveries did not all complete after the restart.");
            await Task.Delay(100);
        }
        output.WriteLine($"Accepted {accepted.Count}; failed publish calls {failedCalls}; at stop: {orphaned} leased, {backlog} pending; " +
            $"drained {clock.Elapsed.TotalSeconds:F1} s after publishing stopped");

        // Every accepted message is stored once, despite retries of calls whose response was lost.
        Assert.True(failedCalls > 0);
        Assert.Equal(accepted.Count, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Messages"));
        Assert.Equal(accepted.Count, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Deliveries WHERE Status = 2"));

        // The leases the stopped broker held lapsed and were delivered again by the new instance in time.
        Assert.True(orphaned > 0, "No delivery was in flight when the broker stopped.");
        var recovered = await QueryAsync<DateTime>("""
            SELECT d.CompletedAt FROM broker.Deliveries d
            WHERE EXISTS (SELECT 1 FROM broker.DeliveryAttempts a WHERE a.DeliveryId = d.DeliveryId AND a.Outcome = 'LeaseExpired')
            """, r => Col<DateTime>(r, 0));
        Assert.Equal(orphaned, recovered.Count);
        var bound = TimeSpan.FromSeconds(LockSeconds + MaintenanceSeconds + RetrySeconds + 2); // + poll and send time
        Assert.All(recovered, completedAt => Assert.True(completedAt - restartedAt < bound,
            $"Recovered {completedAt - restartedAt} after the restart; bound {bound}."));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.DeadLetters"));
    }

    [Fact(DisplayName = "R02 SignalR client reconnects and resubscribes after a broker restart, then receives new deliveries")]
    public async Task R02_SignalRReconnect()
    {
        var (topic, publisher, _) = await ArrangeTopicAsync();
        var (ownerId, ownerKey, _) = await CreateAppClientAsync();
        var subscription = await CreateSubscriptionAsync(topic.TopicId, ownerId, mode: "SignalR", lockSeconds: LockSeconds);

        StartBroker();
        var received = new ConcurrentQueue<Guid>();
        await using var listener = new SignalRDeliveryListener(Broker.Server.BaseAddress, ownerKey,
            (d, _) =>
            {
                received.Enqueue(d.Message.MessageId);
                return Task.FromResult(DeliveryResult.Ack);
            },
            o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                // Each (re)connect goes to whichever broker instance is up.
                o.HttpMessageHandlerFactory = _ => _server?.CreateHandler() ?? new BrokerDownHandler();
            });
        await listener.StartAsync([subscription.SubscriptionId]);

        var before = await PublishAsync(topic.Name, publisher);
        await WaitUntilAsync(() => received.Contains(before.MessageId));

        await StopBrokerAsync();
        await Task.Delay(1_000);
        StartBroker();
        var registry = Broker.Services.GetRequiredService<IConnectionRegistry>();
        var clock = Stopwatch.StartNew();
        await WaitUntilAsync(() => registry.Count(subscription.SubscriptionId) == 1, seconds: 30);
        output.WriteLine($"Rejoined {clock.Elapsed.TotalSeconds:F1} s after the restart");

        var after = await PublishAsync(topic.Name, publisher);
        await WaitUntilAsync(() => received.Contains(after.MessageId));
        var deliveryId = Assert.Single(await GetDeliveriesForMessageAsync(after.MessageId)).DeliveryId;
        await WaitUntilAsync(() => GetDeliveryAsync(deliveryId).Result.State == DeliveryStatus.Completed);
        Assert.Equal(2, received.Distinct().Count());
    }

    private sealed class BrokerDownHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("The broker is down.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Condition was not met in time.");
            await Task.Delay(50);
        }
    }
}
