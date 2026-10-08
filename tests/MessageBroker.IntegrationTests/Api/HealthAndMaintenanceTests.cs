using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MessageBroker.Domain;
using MessageBroker.IntegrationTests.Infrastructure;
using MessageBroker.Worker.Maintenance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MessageBroker.IntegrationTests.Api;

public sealed class HealthAndMaintenanceTests(SqlServerFixture sql) : ApiTest(sql)
{
    private async Task<(HttpStatusCode Status, JsonElement Body)> ReadyAsync(ApiFactory? api = null)
    {
        var response = await (api ?? Api).CreateClient(apiKey: null).GetAsync("/health/ready");
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private static string CheckStatus(JsonElement body, string name) =>
        body.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name).GetProperty("status").GetString()!;

    [Fact(DisplayName = "A10 /health/live is 200 without a key; /health/ready is 503 until the heartbeat is fresh, and again once it is stale")]
    public async Task A10_Health()
    {
        var anonymous = Api.CreateClient(apiKey: null);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health/live")).StatusCode);

        // The test host does not run the loops, so there is no heartbeat yet.
        Assert.DoesNotContain(Api.Services.GetServices<IHostedService>(), s => s is MaintenanceLoop or RetentionLoop);
        var (status, body) = await ReadyAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(("Healthy", "Unhealthy"), (CheckStatus(body, "sql"), CheckStatus(body, "dispatcher")));

        await Operations.WriteHeartbeatAsync("test-instance");
        (status, body) = await ReadyAsync();
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());

        await BackdateAsync("BrokerHeartbeats", "InstanceId", "test-instance", "LastBeatAt", 31);
        (status, body) = await ReadyAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains("31 s old", body.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "dispatcher").GetProperty("description").GetString());
    }

    [Fact(DisplayName = "A10b /health/ready is 503 within a few seconds when SQL Server is unreachable; live stays 200")]
    public async Task A10b_SqlUnreachable()
    {
        await using var broken = new ApiFactory(
            "Server=tcp:127.0.0.1,1;Database=BrokerDb;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=1",
            new Dictionary<string, string?>
            {
                ["Broker:Bootstrap:AdminApiKey"] = "",
                ["Broker:Webhooks:AllowedHosts:0"] = "",
            });

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var (status, body) = await ReadyAsync(broken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("Unhealthy", CheckStatus(body, "sql"));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(6), $"took {timer.Elapsed}");
        Assert.Equal(HttpStatusCode.OK, (await broken.CreateClient(apiKey: null).GetAsync("/health/live")).StatusCode);
    }

    [Fact(DisplayName = "W01 Maintenance pass expires lapsed leases and stale pending deliveries, and writes the heartbeat")]
    public async Task W01_MaintenancePass()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var leasedSub = await CreateSubscriptionAsync(topic.TopicId, subscriber, maxAttempts: 3);
        var ttlSub = await CreateSubscriptionAsync(topic.TopicId, subscriber, ttlSeconds: 60);
        await PublishAsync(topic.Name, publisher);

        var leased = Assert.Single(await Deliveries.LeaseAsync(leasedSub.SubscriptionId, 1, "Pull", null));
        await LapseLeaseAsync(leased.DeliveryId);
        var pending = (await QueryAsync<long>("SELECT DeliveryId FROM broker.Deliveries WHERE SubscriptionId = @id",
            new { id = ttlSub.SubscriptionId })).Single();
        await BackdateAsync("Deliveries", "DeliveryId", pending, "ExpiresAt", 120);

        await Api.Services.GetRequiredService<MaintenanceLoop>().RunOnceAsync(CancellationToken.None);

        var retried = await GetDeliveryAsync(leased.DeliveryId);
        Assert.Equal(DeliveryStatus.Pending, retried.State);
        Assert.Equal("LeaseExpired", Assert.Single(await GetAttemptsAsync(leased.DeliveryId)).Outcome);
        Assert.Equal(DeliveryStatus.DeadLettered, (await GetDeliveryAsync(pending)).State);
        Assert.Equal("Expired", Assert.Single(await GetDeadLettersAsync(pending)).Reason);

        var heartbeat = await Operations.GetLatestHeartbeatAsync();
        Assert.InRange((heartbeat.DbNow - heartbeat.LastBeatAt!.Value).TotalSeconds, 0, 5);
        Assert.Equal(MaintenanceLoop.InstanceId, await ScalarAsync<string>("SELECT InstanceId FROM broker.BrokerHeartbeats"));
    }

    [Fact(DisplayName = "W02 Retention pass purges completed work older than CompletedDays and keeps recent work")]
    public async Task W02_RetentionPass()
    {
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var sub = await CreateSubscriptionAsync(topic.TopicId, subscriber);
        var old = await PublishAsync(topic.Name, publisher);
        var recent = await PublishAsync(topic.Name, publisher);
        foreach (var leased in await Deliveries.LeaseAsync(sub.SubscriptionId, 2, "Pull", null))
            await Deliveries.AckAsync(leased.DeliveryId, leased.LockToken, null);

        var oldDelivery = (await GetDeliveriesForMessageAsync(old.MessageId)).Single().DeliveryId;
        await BackdateAsync("Deliveries", "DeliveryId", oldDelivery, "CompletedAt", 15 * 86_400);
        await BackdateAsync("Messages", "MessageId", old.MessageId, "CreatedAt", 15 * 86_400);

        await Api.Services.GetRequiredService<RetentionLoop>().RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Messages WHERE MessageId = @id", new { id = old.MessageId }));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.DeliveryAttempts WHERE DeliveryId = @oldDelivery", new { oldDelivery }));
        Assert.Single(await GetDeliveriesForMessageAsync(recent.MessageId));
    }
}
