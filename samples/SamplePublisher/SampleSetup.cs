using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MessageBroker.Contracts.Models;
using Samples.Shared;

namespace SamplePublisher;

/// <summary>
/// "SamplePublisher setup": onboards the samples through the admin API, the same steps the runbook
/// describes for a real application. It creates the topic (or reuses it), one application and key per
/// sample, the Publish grant, the three subscriptions and the webhook allowlist entry, plus the
/// notifications topic and applications for the two Blazor samples, then writes
/// samples/samples.local.json for the samples to read. Running it again creates fresh applications.
/// </summary>
public static class SampleSetup
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static async Task<SampleSettings> RunAsync(HttpClient admin, SampleSettings settings, CancellationToken ct = default)
    {
        var topic = await EnsureTopicAsync(admin, settings.TopicName, ct);
        var suffix = Guid.NewGuid().ToString("N")[..6];

        var (publisherId, publisherKey) = await CreateAppAsync(admin, $"sample-publisher-{suffix}", ct);
        await PostAsync(admin, $"api/v1/admin/applications/{publisherId}/permissions",
            new PermissionRequest("Topic", topic.TopicId, "Publish"), ct);
        settings.Publisher = new SampleApp { ApiKey = publisherKey };

        var webhookUrl = new Uri(new Uri(settings.Webhook.Url), "webhooks/payments");
        await AllowHostAsync(admin, webhookUrl.Host, ct);
        var listenUrl = settings.Webhook.Url;
        settings.Webhook = await CreateSubscriberAsync(admin, topic, "Webhook", suffix, webhookUrl.ToString(), ct);
        settings.Webhook.Url = listenUrl;
        settings.SignalR = await CreateSubscriberAsync(admin, topic, "SignalR", suffix, null, ct);
        settings.Pull = await CreateSubscriberAsync(admin, topic, "Pull", suffix, null, ct);

        // The Blazor samples: one app publishes notifications, the other shows them as toasts over SignalR.
        var notifications = await EnsureTopicAsync(admin, settings.NotificationsTopicName, ct);
        var (blazorPublisherId, blazorPublisherKey) = await CreateAppAsync(admin, $"sample-blazor-publisher-{suffix}", ct);
        await PostAsync(admin, $"api/v1/admin/applications/{blazorPublisherId}/permissions",
            new PermissionRequest("Topic", notifications.TopicId, "Publish"), ct);
        settings.BlazorPublisher = new SampleApp { ApiKey = blazorPublisherKey };
        settings.BlazorSubscriber = await CreateSubscriberAsync(admin, notifications, "SignalR", $"blazor-{suffix}", null, ct);
        return settings;
    }

    public static async Task WriteAsync(SampleSettings settings, string path, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, SampleSettings> { [SampleSettings.SectionName] = settings },
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, ct);
    }

    public static HttpClient CreateAdminClient(Uri brokerUrl, string adminKey)
    {
        var http = new HttpClient { BaseAddress = brokerUrl };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("ApiKey", adminKey);
        return http;
    }

    private static async Task<TopicResponse> EnsureTopicAsync(HttpClient admin, string name, CancellationToken ct)
    {
        using var response = await admin.PostAsJsonAsync("api/v1/topics", new CreateTopicRequest(name, null), Json, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var topics = await admin.GetFromJsonAsync<List<TopicResponse>>("api/v1/topics", Json, ct);
            return topics!.Single(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        return await ReadAsync<TopicResponse>(response, ct);
    }

    private static async Task<(Guid AppId, string Key)> CreateAppAsync(HttpClient admin, string name, CancellationToken ct)
    {
        var app = await PostAsync<ApplicationResponse>(admin, "api/v1/admin/applications", new CreateApplicationRequest(name), ct);
        var key = await PostAsync<ApiKeyResponse>(admin, $"api/v1/admin/applications/{app.AppId}/keys", new CreateApiKeyRequest(), ct);
        return (app.AppId, key.ApiKey!);
    }

    private static async Task<SampleApp> CreateSubscriberAsync(
        HttpClient admin, TopicResponse topic, string mode, string suffix, string? webhookUrl, CancellationToken ct)
    {
        var (appId, key) = await CreateAppAsync(admin, $"sample-{mode.ToLowerInvariant()}-{suffix}", ct);
        // Short retries so the failure path reaches the DLQ within a minute.
        var subscription = await PostAsync<SubscriptionResponse>(admin, $"api/v1/topics/{topic.TopicId}/subscriptions",
            new CreateSubscriptionRequest
            {
                Name = $"sample-{mode.ToLowerInvariant()}-{suffix}",
                OwnerAppId = appId,
                DeliveryMode = mode,
                WebhookUrl = webhookUrl,
                // Must stay below the lock duration.
                WebhookTimeoutSeconds = webhookUrl is null ? null : 10,
                MaxAttempts = 3,
                LockDurationSeconds = 30,
                RetryBaseDelaySeconds = 5,
                RetryMaxDelaySeconds = 20,
            }, ct);
        return new SampleApp
        {
            ApiKey = key,
            SubscriptionId = subscription.SubscriptionId,
            Secrets = subscription.WebhookSecret is { } secret ? [secret] : [],
        };
    }

    private static async Task AllowHostAsync(HttpClient admin, string host, CancellationToken ct)
    {
        using var response = await admin.PostAsJsonAsync("api/v1/admin/webhook-hosts", new AllowedHostRequest(host), Json, ct);
        if (response.StatusCode != HttpStatusCode.Conflict)
            await EnsureSuccessAsync(response, ct);
    }

    private static async Task PostAsync(HttpClient http, string path, object body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, body, Json, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private static async Task<T> PostAsync<T>(HttpClient http, string path, object body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, body, Json, ct);
        return await ReadAsync<T>(response, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
    }
}
