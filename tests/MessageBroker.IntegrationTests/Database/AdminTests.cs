using MessageBroker.Application.Persistence;
using MessageBroker.Domain;
using MessageBroker.Infrastructure.Data;
using MessageBroker.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace MessageBroker.IntegrationTests.Database;

public class AdminTests(SqlServerFixture sql) : DatabaseTest(sql)
{
    private static byte[] Hash(int seed) => Enumerable.Repeat((byte)seed, 32).ToArray();

    [Fact(DisplayName = "D23 [Fix 6] Two active keys resolve; a third is refused; deactivated or expired keys do not resolve")]
    public async Task D23_ApiKeys()
    {
        var app = await CreateAppAsync("payments");
        var k1 = await Apps.CreateKeyAsync(Guid.NewGuid(), app, "aaaaaaaaaaaa", Hash(1), null);
        var k2 = await Apps.CreateKeyAsync(Guid.NewGuid(), app, "bbbbbbbbbbbb", Hash(2), null);

        var lookup1 = await Apps.GetKeyByPrefixAsync("aaaaaaaaaaaa");
        Assert.Equal(app, lookup1!.AppId);
        Assert.Equal(Hash(1), lookup1.Hash);
        Assert.Equal("payments", lookup1.AppName);
        Assert.NotNull(await Apps.GetKeyByPrefixAsync("bbbbbbbbbbbb"));

        await ThrowsBrokerAsync(BrokerErrorKind.Conflict, () => Apps.CreateKeyAsync(Guid.NewGuid(), app, "cccccccccccc", Hash(3), null));

        await Apps.DeactivateKeyAsync(app, k1.KeyId);
        Assert.Null(await Apps.GetKeyByPrefixAsync("aaaaaaaaaaaa"));

        var k3 = await Apps.CreateKeyAsync(Guid.NewGuid(), app, "cccccccccccc", Hash(3), DateTime.UtcNow.AddHours(1));
        await ExecAsync("UPDATE broker.ApiKeys SET ExpiresAt = DATEADD(second, -1, SYSUTCDATETIME()) WHERE KeyId = @id", P("id", k3.KeyId));
        Assert.Null(await Apps.GetKeyByPrefixAsync("cccccccccccc"));

        await Apps.SetActiveAsync(app, false);
        Assert.Null(await Apps.GetKeyByPrefixAsync("bbbbbbbbbbbb"));
        Assert.Equal(3, (await Apps.ListKeysAsync(app)).Count);
        Assert.NotEqual(k1.KeyId, k2.KeyId);
    }

    [Fact(DisplayName = "D23b Bootstrap admin is created once and is idempotent")]
    public async Task D23b_Bootstrap()
    {
        for (var i = 0; i < 2; i++)
            await Apps.EnsureBootstrapAdminAsync(Guid.NewGuid(), "admin", Guid.NewGuid(), "adminprefix1", Hash(9));

        var app = Assert.Single(await Apps.ListAsync());
        Assert.True(app.IsAdmin);
        Assert.Single(await Apps.ListKeysAsync(app.AppId));
    }

    [Fact(DisplayName = "D23c Permissions: grant is idempotent, Manage implies others, admins have all")]
    public async Task D23c_Permissions()
    {
        var topic = await CreateTopicAsync();
        var app = await CreateAppAsync();
        var admin = await CreateAppAsync(isAdmin: true);
        var grant = new PermissionRecord { AppId = app, ResourceType = "Topic", ResourceId = topic.TopicId, Permission = "Publish" };

        Assert.False(await Apps.HasPermissionAsync(app, "Topic", topic.TopicId, "Publish"));
        await Apps.GrantAsync(grant);
        await Apps.GrantAsync(grant);
        Assert.Single(await Apps.ListPermissionsAsync(app));
        Assert.True(await Apps.HasPermissionAsync(app, "Topic", topic.TopicId, "Publish"));
        Assert.False(await Apps.HasPermissionAsync(app, "Topic", topic.TopicId, "Receive"));

        await Apps.GrantAsync(grant with { Permission = "Manage" });
        Assert.True(await Apps.HasPermissionAsync(app, "Topic", topic.TopicId, "Receive"));
        Assert.True(await Apps.HasPermissionAsync(admin, "Topic", Guid.NewGuid(), "Publish"));

        await Apps.RevokeAsync(grant);
        await Apps.RevokeAsync(grant with { Permission = "Manage" });
        Assert.False(await Apps.HasPermissionAsync(app, "Topic", topic.TopicId, "Publish"));

        await ThrowsBrokerAsync(BrokerErrorKind.NotFound, () => Apps.GrantAsync(grant with { ResourceId = Guid.NewGuid() }));
    }

    [Fact(DisplayName = "D24 [Fix 7] RotateSecret keeps the previous secret for about 24 hours")]
    public async Task D24_RotateSecret()
    {
        var (topic, _, subscriber) = await ArrangeTopicAsync();
        var sub = await CreateSubscriptionAsync(topic.TopicId, subscriber, mode: "Webhook");
        var now = await DbNowAsync();

        var rotated = await Subscriptions.RotateSecretAsync(sub.SubscriptionId, "new-secret");

        Assert.Equal("new-secret", rotated.WebhookSecret);
        Assert.Equal("protected-secret", rotated.PreviousWebhookSecret);
        Assert.InRange((rotated.PreviousSecretExpiresAt!.Value - now).TotalHours, 23.99, 24.01);

        var pull = await CreateSubscriptionAsync(topic.TopicId, subscriber);
        await ThrowsBrokerAsync(BrokerErrorKind.Validation, () => Subscriptions.RotateSecretAsync(pull.SubscriptionId, "x"));
    }

    [Fact(DisplayName = "D25 [Fix 8] Heartbeat write and latest read on the DB clock")]
    public async Task D25_Heartbeat()
    {
        var empty = await Operations.GetLatestHeartbeatAsync();
        Assert.Null(empty.LastBeatAt);

        await Operations.WriteHeartbeatAsync("host-a");
        await Operations.WriteHeartbeatAsync("host-a");
        var latest = await Operations.GetLatestHeartbeatAsync();

        Assert.NotNull(latest.LastBeatAt);
        Assert.InRange((latest.DbNow - latest.LastBeatAt!.Value).TotalSeconds, 0, 5);
        Assert.Equal(DateTimeKind.Utc, latest.DbNow.Kind);
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.BrokerHeartbeats"));
    }

    [Fact(DisplayName = "D26 Topic and subscription names are unique, case-insensitive")]
    public async Task D26_UniqueNames()
    {
        var topic = await CreateTopicAsync("PaymentProcessed");
        await ThrowsBrokerAsync(BrokerErrorKind.Conflict, () => CreateTopicAsync("paymentprocessed"));

        var owner = await CreateAppAsync();
        await CreateSubscriptionAsync(topic.TopicId, owner, name: "Billing");
        await ThrowsBrokerAsync(BrokerErrorKind.Conflict, () => CreateSubscriptionAsync(topic.TopicId, owner, name: "BILLING"));

        await CreateAppAsync("Ledger");
        await ThrowsBrokerAsync(BrokerErrorKind.Conflict, () => CreateAppAsync("ledger"));

        Assert.Equal(topic.TopicId, (await Topics.GetByNameAsync("PAYMENTPROCESSED"))!.TopicId);
    }

    [Fact(DisplayName = "D26b [Fix 9] Allowlist add, seed (never removes) and remove")]
    public async Task D26b_AllowedHosts()
    {
        await Hosts.AddAsync("billing.internal", null);
        await Hosts.SeedAsync(["Billing.Internal", "ledger.internal", " ", "ledger.internal"]);

        var hosts = (await Hosts.ListAsync()).Select(h => h.Host).ToList();
        Assert.Equal(["billing.internal", "ledger.internal"], hosts);

        await Hosts.RemoveAsync("LEDGER.internal");
        Assert.Single(await Hosts.ListAsync());
        await ThrowsBrokerAsync(BrokerErrorKind.NotFound, () => Hosts.RemoveAsync("ledger.internal"));
    }

    [Fact(DisplayName = "D27 Deploying the schema again is a no-op for migrations and re-applies procedures")]
    public async Task D27_RedeployIsIdempotent()
    {
        var before = await ScalarAsync<int>("SELECT COUNT(*) FROM broker.SchemaVersions");
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        await CreateSubscriptionAsync(topic.TopicId, subscriber);

        new SchemaDeployer(Sql.ConnectionString, NullLogger.Instance).Deploy();
        new SchemaDeployer(Sql.ConnectionString, NullLogger.Instance).Deploy();

        Assert.Equal(before, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.SchemaVersions"));
        Assert.Equal(1, (await PublishAsync(topic.Name, publisher)).DeliveryCount); // data survives, procedures work
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.SchemaVersions WHERE ScriptName LIKE '%0001_Schema.sql'"));
    }
}
