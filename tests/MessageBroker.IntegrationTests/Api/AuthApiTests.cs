using System.Net;
using System.Net.Http.Json;
using MessageBroker.Application.Security;
using MessageBroker.Contracts;
using MessageBroker.Contracts.Models;
using MessageBroker.IntegrationTests.Infrastructure;

namespace MessageBroker.IntegrationTests.Api;

public sealed class AuthApiTests(SqlServerFixture sql) : ApiTest(sql)
{
    [Fact(DisplayName = "A02 Missing, malformed, unknown, deactivated or expired keys are 401")]
    public async Task A02_Unauthenticated()
    {
        var topic = await CreateTopicViaApiAsync();

        var none = await PublishViaApiAsync(Api.CreateClient(apiKey: null), topic.Name);
        await ProblemAsync(none, HttpStatusCode.Unauthorized, ProblemTypes.Unauthorized);
        Assert.Equal("ApiKey", none.Headers.WwwAuthenticate.Single().Scheme);

        await ProblemAsync(await PublishViaApiAsync(Api.CreateClient("not-a-key"), topic.Name),
            HttpStatusCode.Unauthorized, ProblemTypes.Unauthorized);
        await ProblemAsync(await PublishViaApiAsync(Api.CreateClient(ApiKeys.Generate().Key), topic.Name),
            HttpStatusCode.Unauthorized, ProblemTypes.Unauthorized);

        var basic = Api.CreateClient(apiKey: null);
        basic.DefaultRequestHeaders.Authorization = new("Basic", "dXNlcjpwYXNz");
        await ProblemAsync(await basic.GetAsync("/api/v1/topics"), HttpStatusCode.Unauthorized, ProblemTypes.Unauthorized);

        // Right prefix, wrong secret.
        var (appId, key, client) = await CreateAppClientAsync();
        var tampered = key[..^4] + (key.EndsWith("AAAA") ? "BBBB" : "AAAA");
        await ProblemAsync(await Api.CreateClient(tampered).GetAsync("/api/v1/topics"), HttpStatusCode.Unauthorized, ProblemTypes.Unauthorized);

        // Deactivated key.
        var keys = await ReadAsync<List<ApiKeyResponse>>(await Admin.GetAsync($"/api/v1/admin/applications/{appId}/keys"));
        await ExpectAsync(Admin.DeleteAsync($"/api/v1/admin/applications/{appId}/keys/{keys.Single().KeyId}"), HttpStatusCode.NoContent);
        await ProblemAsync(await client.GetAsync("/api/v1/topics"), HttpStatusCode.Unauthorized, ProblemTypes.Unauthorized);

        // Deactivated application.
        var (otherId, _, other) = await CreateAppClientAsync();
        await ExpectAsync(Admin.PatchAsJsonAsync($"/api/v1/admin/applications/{otherId}", new UpdateApplicationRequest(false)), HttpStatusCode.OK);
        await ProblemAsync(await other.GetAsync("/api/v1/topics"), HttpStatusCode.Unauthorized, ProblemTypes.Unauthorized);

        // Expired key (expiry moved into the past).
        var (expiringId, _, expiring) = await CreateAppClientAsync();
        await ExecAsync("UPDATE broker.ApiKeys SET ExpiresAt = DATEADD(second, -1, SYSUTCDATETIME()) WHERE AppId = @expiringId", new { expiringId });
        await ProblemAsync(await expiring.GetAsync("/api/v1/topics"), HttpStatusCode.Unauthorized, ProblemTypes.Unauthorized);
    }

    [Fact(DisplayName = "A02b Publishing without permission is 403; non-admins get 403 on admin endpoints")]
    public async Task A02b_Forbidden()
    {
        var topic = await CreateTopicViaApiAsync();
        var (appId, _, client) = await CreateAppClientAsync();

        var denied = await ProblemAsync(await PublishViaApiAsync(client, topic.Name), HttpStatusCode.Forbidden, ProblemTypes.Forbidden);
        Assert.Contains("publish", denied.GetProperty("detail").GetString());

        // Receive on the topic is not Publish.
        var subscription = await CreateSubscriptionAsync(topic.TopicId, appId);
        await ProblemAsync(await PublishViaApiAsync(client, topic.Name), HttpStatusCode.Forbidden, ProblemTypes.Forbidden);

        // Manage implies Publish.
        await GrantViaApiAsync(appId, "Topic", topic.TopicId, "Manage");
        await ExpectAsync(PublishViaApiAsync(client, topic.Name), HttpStatusCode.Created);

        foreach (var call in new Func<Task<HttpResponseMessage>>[]
        {
            () => client.GetAsync("/api/v1/topics"),
            () => client.PostAsJsonAsync("/api/v1/topics", new CreateTopicRequest("sneaky", null)),
            () => client.DeleteAsync($"/api/v1/topics/{topic.TopicId}"),
            () => client.GetAsync($"/api/v1/subscriptions/{subscription.SubscriptionId}"),
            () => client.PatchAsJsonAsync($"/api/v1/subscriptions/{subscription.SubscriptionId}", new UpdateSubscriptionRequest { Status = "Paused" }),
            () => client.PostAsync($"/api/v1/subscriptions/{subscription.SubscriptionId}/webhook-secret", null),
            () => client.GetAsync("/api/v1/admin/applications"),
            () => client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("escalate", true)),
            () => client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/keys", new CreateApiKeyRequest()),
            () => client.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/permissions", new PermissionRequest("Topic", topic.TopicId, "Manage")),
            () => client.GetAsync("/api/v1/admin/webhook-hosts"),
            () => client.GetAsync("/api/v1/messages?correlationId=PAY-12345"),
        })
        {
            await ProblemAsync(await call(), HttpStatusCode.Forbidden, ProblemTypes.Forbidden);
        }

        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Topics"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Applications WHERE Name = 'escalate'"));
    }

    [Fact(DisplayName = "A02c [Fix 6] Keys: two may be active, a third is 409, both active keys authenticate")]
    public async Task A02c_KeyRotation()
    {
        var (appId, firstKey, _) = await CreateAppClientAsync();

        var second = await ReadAsync<ApiKeyResponse>(await Admin.PostAsJsonAsync(
            $"/api/v1/admin/applications/{appId}/keys", new CreateApiKeyRequest()), HttpStatusCode.Created);
        Assert.StartsWith(second.Prefix + "_", second.ApiKey);

        await ProblemAsync(await Admin.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/keys", new CreateApiKeyRequest()),
            HttpStatusCode.Conflict, ProblemTypes.Conflict);

        var topic = await CreateTopicViaApiAsync();
        await GrantViaApiAsync(appId, "Topic", topic.TopicId, "Publish");
        await ExpectAsync(PublishViaApiAsync(Api.CreateClient(firstKey), topic.Name), HttpStatusCode.Created);
        await ExpectAsync(PublishViaApiAsync(Api.CreateClient(second.ApiKey), topic.Name), HttpStatusCode.Created);

        // The plain key is never listed or stored.
        var listed = await ReadAsync<List<ApiKeyResponse>>(await Admin.GetAsync($"/api/v1/admin/applications/{appId}/keys"));
        Assert.Equal(2, listed.Count);
        Assert.All(listed, k => Assert.Null(k.ApiKey));
        Assert.Equal(32, await ScalarAsync<int>("SELECT MAX(DATALENGTH(Hash)) FROM broker.ApiKeys WHERE AppId = @appId", new { appId }));

        // An admin cannot lock itself out.
        var self = await ReadAsync<List<ApplicationResponse>>(await Admin.GetAsync("/api/v1/admin/applications"));
        var adminId = self.Single(a => a.IsAdmin).AppId;
        await ProblemAsync(await Admin.PatchAsJsonAsync($"/api/v1/admin/applications/{adminId}", new UpdateApplicationRequest(false)),
            HttpStatusCode.BadRequest, ProblemTypes.Validation);
    }
}
