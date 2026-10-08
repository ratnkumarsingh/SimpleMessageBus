using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MessageBroker.Contracts.Models;

namespace MessageBroker.IntegrationTests.Infrastructure;

/// <summary>
/// Base for tests that go through HTTP. Data is reset before each test, then the startup seeding runs
/// again so the bootstrap admin key and the configured allowlist host exist.
/// </summary>
public abstract class ApiTest(SqlServerFixture sql) : DatabaseTest(sql)
{
    protected ApiFactory Api => Sql.Api;

    protected HttpClient Admin => Api.CreateClient(ApiFactory.AdminKey);

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await Api.RunStartupTasksAsync();
    }

    /// <summary>Registers an application through the admin API and issues it a key.</summary>
    protected async Task<(Guid AppId, string Key, HttpClient Client)> CreateAppClientAsync(string? name = null, bool isAdmin = false)
    {
        var app = await ReadAsync<ApplicationResponse>(await Admin.PostAsJsonAsync("/api/v1/admin/applications",
            new CreateApplicationRequest(name ?? $"app-{Guid.NewGuid():N}"[..20], isAdmin)), HttpStatusCode.Created);
        var key = await ReadAsync<ApiKeyResponse>(await Admin.PostAsJsonAsync(
            $"/api/v1/admin/applications/{app.AppId}/keys", new CreateApiKeyRequest()), HttpStatusCode.Created);
        return (app.AppId, key.ApiKey!, Api.CreateClient(key.ApiKey));
    }

    protected async Task<TopicResponse> CreateTopicViaApiAsync(string? name = null, int? defaultTtl = null) =>
        await ReadAsync<TopicResponse>(await Admin.PostAsJsonAsync("/api/v1/topics",
            new CreateTopicRequest(name ?? $"topic-{Guid.NewGuid():N}"[..20], defaultTtl)), HttpStatusCode.Created);

    protected Task GrantViaApiAsync(Guid appId, string resourceType, Guid resourceId, string permission) =>
        ExpectAsync(Admin.PostAsJsonAsync($"/api/v1/admin/applications/{appId}/permissions",
            new PermissionRequest(resourceType, resourceId, permission)), HttpStatusCode.NoContent);

    protected static Task<HttpResponseMessage> PublishViaApiAsync(
        HttpClient client, string topicName, object? body = null, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/topics/{topicName}/messages")
        {
            Content = JsonContent.Create(body ?? new
            {
                messageType = "PaymentProcessed.v1",
                correlationId = "PAY-12345",
                properties = new { tenant = "in-01" },
                payload = new { paymentId = "PAY-12345", amount = 1500.00, currency = "INR" },
            }),
        };
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request);
    }

    protected static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        await AssertStatusAsync(response, expected);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    protected static async Task ExpectAsync(Task<HttpResponseMessage> call, HttpStatusCode expected) =>
        await AssertStatusAsync(await call, expected);

    /// <summary>Asserts an RFC 9457 problem response and returns its JSON.</summary>
    protected static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode expected, string type)
    {
        await AssertStatusAsync(response, expected);
        Assert.True(response.Content.Headers.ContentType?.MediaType == "application/problem+json", $"{response.Content.Headers.ContentType} {await response.Content.ReadAsStringAsync()}");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)expected, problem.GetProperty("status").GetInt32());
        Assert.Equal(type, problem.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("title").GetString()));
        return problem;
    }

    private static async Task AssertStatusAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
            Assert.Fail($"Expected {(int)expected} but got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
}
