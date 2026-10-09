using System.Net;
using System.Net.Http.Json;
using MessageBroker.Application.Security;
using MessageBroker.Contracts;
using MessageBroker.Contracts.Models;
using MessageBroker.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace MessageBroker.IntegrationTests.Api;

public sealed class TopologyApiTests(SqlServerFixture sql) : ApiTest(sql)
{
    [Fact(DisplayName = "A06 Topic CRUD: create, list, read, change TTL, delete only without subscriptions")]
    public async Task A06_TopicCrud()
    {
        var created = await Admin.PostAsJsonAsync("/api/v1/topics", new CreateTopicRequest("PaymentProcessed", 600));
        var topic = await ReadAsync<TopicResponse>(created, HttpStatusCode.Created);
        Assert.Equal(("PaymentProcessed", 600), (topic.Name, topic.DefaultTtlSeconds));
        Assert.EndsWith($"/api/v1/topics/{topic.TopicId}", created.Headers.Location!.ToString());

        Assert.Equal(topic, await ReadAsync<TopicResponse>(await Admin.GetAsync($"/api/v1/topics/{topic.TopicId}")));
        Assert.Contains(topic, await ReadAsync<List<TopicResponse>>(await Admin.GetAsync("/api/v1/topics")));

        var changed = await ReadAsync<TopicResponse>(await Admin.PatchAsJsonAsync($"/api/v1/topics/{topic.TopicId}", new UpdateTopicRequest(null)));
        Assert.Null(changed.DefaultTtlSeconds);

        await ProblemAsync(await Admin.PostAsJsonAsync("/api/v1/topics", new CreateTopicRequest("bad name", null)),
            HttpStatusCode.BadRequest, ProblemTypes.Validation);
        await ProblemAsync(await Admin.PostAsJsonAsync("/api/v1/topics", new CreateTopicRequest("ttl-zero", 0)),
            HttpStatusCode.BadRequest, ProblemTypes.Validation);

        var owner = await CreateAppAsync();
        var subscription = await ReadAsync<SubscriptionResponse>(await Admin.PostAsJsonAsync(
            $"/api/v1/topics/{topic.TopicId}/subscriptions",
            new CreateSubscriptionRequest { Name = "ledger", OwnerAppId = owner, DeliveryMode = "Pull" }), HttpStatusCode.Created);

        await ProblemAsync(await Admin.DeleteAsync($"/api/v1/topics/{topic.TopicId}"), HttpStatusCode.Conflict, ProblemTypes.Conflict);
        await ExpectAsync(Admin.DeleteAsync($"/api/v1/subscriptions/{subscription.SubscriptionId}"), HttpStatusCode.NoContent);
        await ExpectAsync(Admin.DeleteAsync($"/api/v1/topics/{topic.TopicId}"), HttpStatusCode.NoContent);

        await ProblemAsync(await Admin.GetAsync($"/api/v1/topics/{topic.TopicId}"), HttpStatusCode.NotFound, ProblemTypes.NotFound);
        await ProblemAsync(await Admin.DeleteAsync($"/api/v1/topics/{topic.TopicId}"), HttpStatusCode.NotFound, ProblemTypes.NotFound);
    }

    [Fact(DisplayName = "A06b Subscription create takes defaults, lists with counts, pauses, resumes, changes and deletes")]
    public async Task A06b_SubscriptionLifecycle()
    {
        var topic = await CreateTopicViaApiAsync();
        var owner = await CreateAppAsync();

        var created = await Admin.PostAsJsonAsync($"/api/v1/topics/{topic.TopicId}/subscriptions",
            new CreateSubscriptionRequest { Name = "ledger", OwnerAppId = owner, DeliveryMode = "pull", MaxAttempts = 6 });
        var subscription = await ReadAsync<SubscriptionResponse>(created, HttpStatusCode.Created);
        Assert.EndsWith($"/api/v1/subscriptions/{subscription.SubscriptionId}", created.Headers.Location!.ToString());
        Assert.Equal(("Pull", "Active", 6, 60, 30, 900, 8), (subscription.DeliveryMode, subscription.Status, subscription.MaxAttempts,
            subscription.LockDurationSeconds, subscription.RetryBaseDelaySeconds, subscription.RetryMaxDelaySeconds,
            subscription.MaxConcurrentDeliveries));
        Assert.Null(subscription.WebhookSecret);

        // The owner can receive from its own subscription.
        Assert.True(await Apps.HasPermissionAsync(owner, "Subscription", subscription.SubscriptionId, "Receive"));

        // Counts per delivery state.
        var publisher = await CreateAppAsync();
        await GrantPublishAsync(publisher, topic.TopicId);
        await PublishAsync(topic.Name, publisher);
        await PublishAsync(topic.Name, publisher);
        var listed = Assert.Single(await ReadAsync<List<SubscriptionResponse>>(await Admin.GetAsync($"/api/v1/topics/{topic.TopicId}/subscriptions")));
        Assert.Equal(new DeliveryCountsResponse(2, 0, 0), listed.Counts);

        // Pause and resume.
        var paused = await ReadAsync<SubscriptionResponse>(await Admin.PatchAsJsonAsync(
            $"/api/v1/subscriptions/{subscription.SubscriptionId}", new UpdateSubscriptionRequest { Status = "Paused" }));
        Assert.Equal("Paused", paused.Status);
        Assert.Empty(await Deliveries.LeaseAsync(subscription.SubscriptionId, 10, "Pull", owner));
        var resumed = await ReadAsync<SubscriptionResponse>(await Admin.PatchAsJsonAsync(
            $"/api/v1/subscriptions/{subscription.SubscriptionId}", new UpdateSubscriptionRequest { Status = "Active" }));
        Assert.Equal("Active", resumed.Status);

        // Partial change keeps the other settings; TTL can be set and cleared.
        var tuned = await ReadAsync<SubscriptionResponse>(await Admin.PatchAsJsonAsync(
            $"/api/v1/subscriptions/{subscription.SubscriptionId}", new UpdateSubscriptionRequest { LockDurationSeconds = 120, TtlSeconds = 3600 }));
        Assert.Equal((120, 6, 3600), (tuned.LockDurationSeconds, tuned.MaxAttempts, tuned.TtlSeconds!.Value));
        var cleared = await ReadAsync<SubscriptionResponse>(await Admin.PatchAsJsonAsync(
            $"/api/v1/subscriptions/{subscription.SubscriptionId}", new UpdateSubscriptionRequest { ClearTtl = true }));
        Assert.Null(cleared.TtlSeconds);

        // Invalid changes.
        foreach (var bad in new[]
        {
            new UpdateSubscriptionRequest { Status = "Deleted" },
            new UpdateSubscriptionRequest { LockDurationSeconds = 601 },
            new UpdateSubscriptionRequest { RetryBaseDelaySeconds = 1000 },
            new UpdateSubscriptionRequest { WebhookUrl = "https://hooks.internal/x" }, // pull subscriptions take no URL
        })
        {
            await ProblemAsync(await Admin.PatchAsJsonAsync($"/api/v1/subscriptions/{subscription.SubscriptionId}", bad),
                HttpStatusCode.BadRequest, ProblemTypes.Validation);
        }

        // Duplicate name on the same topic.
        await ProblemAsync(await Admin.PostAsJsonAsync($"/api/v1/topics/{topic.TopicId}/subscriptions",
                new CreateSubscriptionRequest { Name = "LEDGER", OwnerAppId = owner, DeliveryMode = "Pull" }),
            HttpStatusCode.Conflict, ProblemTypes.Conflict);

        // Delete cancels deliveries and hides the subscription.
        await ExpectAsync(Admin.DeleteAsync($"/api/v1/subscriptions/{subscription.SubscriptionId}"), HttpStatusCode.NoContent);
        await ProblemAsync(await Admin.GetAsync($"/api/v1/subscriptions/{subscription.SubscriptionId}"), HttpStatusCode.NotFound, ProblemTypes.NotFound);
        Assert.Equal(2, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Deliveries WHERE Status = 4"));
    }

    [Fact(DisplayName = "A06c Create validation: name, owner, mode, settings; unknown topic or owner is 404")]
    public async Task A06c_CreateValidation()
    {
        var topic = await CreateTopicViaApiAsync();
        var owner = await CreateAppAsync();
        var url = $"/api/v1/topics/{topic.TopicId}/subscriptions";

        foreach (var bad in new[]
        {
            new CreateSubscriptionRequest { Name = "bad name", OwnerAppId = owner, DeliveryMode = "Pull" },
            new CreateSubscriptionRequest { Name = "no-owner", DeliveryMode = "Pull" },
            new CreateSubscriptionRequest { Name = "no-mode", OwnerAppId = owner },
            new CreateSubscriptionRequest { Name = "numeric-mode", OwnerAppId = owner, DeliveryMode = "0" },
            new CreateSubscriptionRequest { Name = "lock", OwnerAppId = owner, DeliveryMode = "Pull", LockDurationSeconds = 0 },
            new CreateSubscriptionRequest { Name = "attempts", OwnerAppId = owner, DeliveryMode = "Pull", MaxAttempts = 0 },
            new CreateSubscriptionRequest { Name = "signalr-url", OwnerAppId = owner, DeliveryMode = "SignalR", WebhookUrl = "https://hooks.internal/a" },
        })
        {
            await ProblemAsync(await Admin.PostAsJsonAsync(url, bad), HttpStatusCode.BadRequest, ProblemTypes.Validation);
        }

        await ProblemAsync(await Admin.PostAsJsonAsync($"/api/v1/topics/{Guid.NewGuid()}/subscriptions",
                new CreateSubscriptionRequest { Name = "orphan", OwnerAppId = owner, DeliveryMode = "Pull" }),
            HttpStatusCode.NotFound, ProblemTypes.NotFound);
        await ProblemAsync(await Admin.PostAsJsonAsync(url,
                new CreateSubscriptionRequest { Name = "ghost-owner", OwnerAppId = Guid.NewGuid(), DeliveryMode = "Pull" }),
            HttpStatusCode.NotFound, ProblemTypes.NotFound);
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Subscriptions"));
    }

    [Fact(DisplayName = "A06d [Fix 9] Webhook URL must be HTTPS and on the allowlist; secret is returned once and stored encrypted")]
    public async Task A06d_WebhookRules()
    {
        var topic = await CreateTopicViaApiAsync();
        var owner = await CreateAppAsync();
        var url = $"/api/v1/topics/{topic.TopicId}/subscriptions";
        CreateSubscriptionRequest Webhook(string name, string webhookUrl) =>
            new() { Name = name, OwnerAppId = owner, DeliveryMode = "Webhook", WebhookUrl = webhookUrl };

        var http = await ProblemAsync(await Admin.PostAsJsonAsync(url, Webhook("plain-http", $"http://{ApiFactory.SeededHost}/receive")),
            HttpStatusCode.BadRequest, ProblemTypes.Validation);
        Assert.Contains("HTTPS", http.GetProperty("errors").GetProperty("webhookUrl")[0].GetString());

        var notAllowed = await ProblemAsync(await Admin.PostAsJsonAsync(url, Webhook("elsewhere", "https://evil.example/receive")),
            HttpStatusCode.BadRequest, ProblemTypes.Validation);
        Assert.Contains("allowlist", notAllowed.GetProperty("errors").GetProperty("webhookUrl")[0].GetString());

        await ProblemAsync(await Admin.PostAsJsonAsync(url, Webhook("no-url", "")), HttpStatusCode.BadRequest, ProblemTypes.Validation);

        // Allowlisted host, matched case-insensitively.
        var subscription = await ReadAsync<SubscriptionResponse>(await Admin.PostAsJsonAsync(url,
            Webhook("billing", "https://HOOKS.internal/receive") with { WebhookTimeoutSeconds = 10 }), HttpStatusCode.Created);
        Assert.Equal(10, subscription.WebhookTimeoutSeconds);
        Assert.False(string.IsNullOrEmpty(subscription.WebhookSecret));

        // Stored encrypted; reads never return it.
        var stored = await ScalarAsync<string>("SELECT WebhookSecret FROM broker.Subscriptions WHERE SubscriptionId = @id", P("id", subscription.SubscriptionId));
        Assert.NotEqual(subscription.WebhookSecret, stored);
        var protector = Api.Services.GetRequiredService<ISecretProtector>();
        Assert.Equal(subscription.WebhookSecret, protector.Unprotect(stored));
        Assert.Null((await ReadAsync<SubscriptionResponse>(await Admin.GetAsync($"/api/v1/subscriptions/{subscription.SubscriptionId}"))).WebhookSecret);

        // Changing to a host that is not allowed fails; pausing does not re-check the existing URL.
        await ProblemAsync(await Admin.PatchAsJsonAsync($"/api/v1/subscriptions/{subscription.SubscriptionId}",
            new UpdateSubscriptionRequest { WebhookUrl = "https://evil.example/receive" }), HttpStatusCode.BadRequest, ProblemTypes.Validation);
        await ExpectAsync(Admin.DeleteAsync($"/api/v1/admin/webhook-hosts/{ApiFactory.SeededHost}"), HttpStatusCode.NoContent);
        await ExpectAsync(Admin.PatchAsJsonAsync($"/api/v1/subscriptions/{subscription.SubscriptionId}",
            new UpdateSubscriptionRequest { Status = "Paused" }), HttpStatusCode.OK);

        // Timeout must stay below the lock duration.
        await ProblemAsync(await Admin.PatchAsJsonAsync($"/api/v1/subscriptions/{subscription.SubscriptionId}",
            new UpdateSubscriptionRequest { WebhookTimeoutSeconds = 60 }), HttpStatusCode.BadRequest, ProblemTypes.Validation);
    }

    [Fact(DisplayName = "A06e [Fix 7] Rotating the webhook secret returns a new one and keeps the old for 24 hours")]
    public async Task A06e_RotateSecret()
    {
        var topic = await CreateTopicViaApiAsync();
        var owner = await CreateAppAsync();
        var subscription = await ReadAsync<SubscriptionResponse>(await Admin.PostAsJsonAsync($"/api/v1/topics/{topic.TopicId}/subscriptions",
            new CreateSubscriptionRequest { Name = "billing", OwnerAppId = owner, DeliveryMode = "Webhook", WebhookUrl = "https://hooks.internal/r" }),
            HttpStatusCode.Created);

        var rotated = await ReadAsync<SubscriptionResponse>(await Admin.PostAsync($"/api/v1/subscriptions/{subscription.SubscriptionId}/webhook-secret", null));
        Assert.False(string.IsNullOrEmpty(rotated.WebhookSecret));
        Assert.NotEqual(subscription.WebhookSecret, rotated.WebhookSecret);
        Assert.InRange((rotated.PreviousSecretExpiresAt!.Value - await DbNowAsync()).TotalHours, 23.9, 24.1);

        var protector = Api.Services.GetRequiredService<ISecretProtector>();
        var previous = await ScalarAsync<string>("SELECT PreviousWebhookSecret FROM broker.Subscriptions WHERE SubscriptionId = @id", P("id", subscription.SubscriptionId));
        Assert.Equal(subscription.WebhookSecret, protector.Unprotect(previous));

        // Pull subscriptions have no secret.
        var pull = await ReadAsync<SubscriptionResponse>(await Admin.PostAsJsonAsync($"/api/v1/topics/{topic.TopicId}/subscriptions",
            new CreateSubscriptionRequest { Name = "ledger", OwnerAppId = owner, DeliveryMode = "Pull" }), HttpStatusCode.Created);
        await ProblemAsync(await Admin.PostAsync($"/api/v1/subscriptions/{pull.SubscriptionId}/webhook-secret", null),
            HttpStatusCode.BadRequest, ProblemTypes.Validation);
    }
}
