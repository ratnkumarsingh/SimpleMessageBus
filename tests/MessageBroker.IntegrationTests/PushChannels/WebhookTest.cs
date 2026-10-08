using System.Text.Json;
using MessageBroker.Application.Persistence;
using MessageBroker.Application.Security;
using MessageBroker.Contracts.Models;
using MessageBroker.Contracts.Webhooks;
using MessageBroker.IntegrationTests.Infrastructure;
using MessageBroker.Worker.Maintenance;
using MessageBroker.Worker.Push;
using Microsoft.Extensions.DependencyInjection;

namespace MessageBroker.IntegrationTests.PushChannels;

/// <summary>
/// Base for webhook tests: a <see cref="FakeWebhookEndpoint"/> stands in for the subscriber's endpoint, and the
/// lease loop is driven one pass at a time.
/// </summary>
public abstract class WebhookTest(SqlServerFixture sql) : ApiTest(sql)
{
    protected const string HookPath = "/hook";
    protected const string Secret = "whsec-current-0123456789";

    protected FakeWebhookEndpoint Endpoint { get; private set; } = null!;

    protected string HookUrl => Endpoint.Url + HookPath;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Endpoint = await FakeWebhookEndpoint.StartAsync();
    }

    public override async Task DisposeAsync()
    {
        await Endpoint.DisposeAsync();
        await base.DisposeAsync();
    }

    protected void Respond(int status, string? body = null, TimeSpan? delay = null, string path = HookPath,
        IReadOnlyDictionary<string, string>? headers = null) =>
        Endpoint.Respond(path, status, body, delay, headers);

    protected string Protect(string secret) => (Api.Services.GetRequiredService<ISecretProtector>()).Protect(secret);

    protected Task<SubscriptionRecord> CreateWebhookSubscriptionAsync(
        Guid topicId, Guid ownerAppId, int maxAttempts = 4, int timeout = 30, int maxConcurrent = 8, int retryBase = 30,
        ApiFactory? api = null) =>
        CreateSubscriptionAsync(topicId, ownerAppId, mode: "Webhook", maxAttempts: maxAttempts, retryBase: retryBase,
            maxConcurrent: maxConcurrent, webhookUrl: HookUrl,
            protectedSecret: (api ?? Api).Services.GetRequiredService<ISecretProtector>().Protect(Secret), webhookTimeout: timeout);

    protected Task<int> LeasePassAsync(ApiFactory? api = null) =>
        (api ?? Api).Services.GetRequiredService<LeaseLoop>().RunOnceAsync(CancellationToken.None);

    protected Task MaintenancePassAsync() =>
        Api.Services.GetRequiredService<MaintenanceLoop>().RunOnceAsync(CancellationToken.None);

    /// <summary>Skips the backoff of every Pending delivery of the subscription.</summary>
    protected Task MakeAllDueAsync(Guid subscriptionId) =>
        ExecAsync("UPDATE broker.Deliveries SET AvailableAt = DATEADD(second, -1, SYSUTCDATETIME()) WHERE SubscriptionId = @subscriptionId AND Status = 0",
            new { subscriptionId });

    protected IReadOnlyList<RecordedCall> Calls(string path = HookPath) => Endpoint.Calls(path);

    protected static string Header(RecordedCall call, string name) => call.Header(name);

    protected static Delivery Body(RecordedCall call) =>
        JsonSerializer.Deserialize<Delivery>(call.Body, JsonSerializerOptions.Web)!;

    protected static bool Verifies(RecordedCall call, params string[] secrets) =>
        WebhookSignature.Verify(call.Header(WebhookHeaders.Signature), call.Header(WebhookHeaders.Timestamp),
            call.Body, DateTimeOffset.UtcNow, secrets);
}
