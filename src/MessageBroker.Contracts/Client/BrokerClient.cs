using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MessageBroker.Contracts.Models;
using Microsoft.Extensions.DependencyInjection;

namespace MessageBroker.Contracts.Client;

public sealed record PublishResult(Guid MessageId, int DeliveryCount, bool IsDuplicate);

/// <summary>
/// An error response from the broker, with its RFC 9457 problem details. Check <see cref="IsLeaseLost"/>
/// (do not retry the ACK) and <see cref="IsTransient"/> (retry later; publishers keep the same Idempotency-Key).
/// </summary>
public sealed class BrokerApiException(HttpStatusCode statusCode, string? problemType, string? title, string? detail)
    : Exception(detail ?? title ?? $"The broker answered {(int)statusCode}.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? ProblemType { get; } = problemType;
    public string? Title { get; } = title;
    public string? Detail { get; } = detail;

    public bool IsLeaseLost => StatusCode == HttpStatusCode.Gone;
    public bool IsTransient => StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout;
}

public sealed class BrokerClientOptions
{
    /// <summary>The broker's base address, e.g. https://broker.internal/.</summary>
    public Uri? BaseAddress { get; set; }
    public string ApiKey { get; set; } = "";
}

/// <summary>
/// Typed client for the broker REST API (publish, pull, traceability and the admin reads the dashboard uses). The HttpClient must carry the
/// base address and the "Authorization: ApiKey" header; <see cref="BrokerClientExtensions.AddBrokerClient"/> sets both.
/// </summary>
public sealed class BrokerClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    /// <summary>Creates a client over an existing HttpClient, adding the API key header.</summary>
    public static BrokerClient Create(HttpClient http, string apiKey)
    {
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("ApiKey", apiKey);
        return new BrokerClient(http);
    }

    /// <summary>Publishes one message. A repeat with the same <paramref name="idempotencyKey"/> returns the original ID.</summary>
    public async Task<PublishResult> PublishAsync(string topicName, PublishRequest request, string? idempotencyKey = null, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"api/v1/topics/{Uri.EscapeDataString(topicName)}/messages")
        {
            Content = JsonContent.Create(request, options: Json),
        };
        if (idempotencyKey is not null)
            message.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await http.SendAsync(message, ct);
        var body = await ReadAsync<PublishResponse>(response, ct);
        return new PublishResult(body.MessageId, body.DeliveryCount, response.StatusCode == HttpStatusCode.OK);
    }

    /// <summary>Leases up to <paramref name="maxMessages"/> (1–32), waiting up to <paramref name="waitSeconds"/> (0–30).</summary>
    public async Task<IReadOnlyList<Delivery>> ReceiveAsync(Guid subscriptionId, int maxMessages = 1, int waitSeconds = 0, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/subscriptions/{subscriptionId}/receive",
            new ReceiveRequest(maxMessages, waitSeconds), Json, ct);
        return await ReadAsync<List<Delivery>>(response, ct);
    }

    /// <exception cref="BrokerApiException">With <see cref="BrokerApiException.IsLeaseLost"/> when the lease was lost.</exception>
    public Task AckAsync(Delivery delivery, CancellationToken ct = default) =>
        PostAsync($"api/v1/deliveries/{delivery.DeliveryId}/ack", new AckRequest(delivery.LockToken), ct);

    public Task NackAsync(Delivery delivery, string? errorCode, string? errorMessage, bool deadLetter = false, CancellationToken ct = default) =>
        PostAsync($"api/v1/deliveries/{delivery.DeliveryId}/nack",
            new NackRequest(delivery.LockToken, errorCode, errorMessage, DeadLetter: deadLetter), ct);

    public Task RenewAsync(Delivery delivery, CancellationToken ct = default) =>
        PostAsync($"api/v1/deliveries/{delivery.DeliveryId}/renew", new RenewRequest(delivery.LockToken), ct);

    public async Task<MessageResponse> GetMessageAsync(Guid messageId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/v1/messages/{messageId}", ct);
        return await ReadAsync<MessageResponse>(response, ct);
    }

    // ---- admin reads (Admin keys; used by the dashboard) ----

    public Task<OverviewResponse> GetOverviewAsync(int? windowMinutes = null, CancellationToken ct = default) =>
        GetAsync<OverviewResponse>("api/v1/admin/overview" + Query(("windowMinutes", windowMinutes)), ct);

    public Task<MessageSearchResponse> SearchMessagesAsync(MessageSearchRequest request, CancellationToken ct = default) =>
        GetAsync<MessageSearchResponse>("api/v1/admin/messages" + Query(
            ("topicId", request.TopicId), ("status", request.Status), ("messageType", request.MessageType),
            ("correlationId", request.CorrelationId), ("publisherAppId", request.PublisherAppId),
            ("from", request.From), ("to", request.To), ("cursor", request.Cursor), ("pageSize", request.PageSize)), ct);

    public Task<IReadOnlyList<MessageSummaryResponse>> ListMessagesByCorrelationAsync(string correlationId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<MessageSummaryResponse>>("api/v1/messages" + Query(("correlationId", correlationId)), ct);

    public Task<DeadLetterPage> SearchDeadLettersAsync(DeadLetterSearchRequest request, CancellationToken ct = default) =>
        GetAsync<DeadLetterPage>("api/v1/admin/deadletters" + Query(
            ("topicId", request.TopicId), ("subscriptionId", request.SubscriptionId), ("reason", request.Reason),
            ("from", request.From), ("to", request.To), ("includeRequeued", request.IncludeRequeued ? true : null),
            ("before", request.Before), ("pageSize", request.PageSize)), ct);

    /// <summary>Returns a dead-lettered delivery to Pending with a fresh attempt budget.</summary>
    public async Task RequeueDeadLetterAsync(long deliveryId, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"api/v1/deadletters/{deliveryId}/requeue", null, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public Task<IReadOnlyList<TopicResponse>> ListTopicsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<TopicResponse>>("api/v1/topics", ct);

    public Task<IReadOnlyList<SubscriptionResponse>> ListSubscriptionsAsync(Guid topicId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<SubscriptionResponse>>($"api/v1/topics/{topicId}/subscriptions", ct);

    public Task<IReadOnlyList<ApplicationResponse>> ListApplicationsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ApplicationResponse>>("api/v1/admin/applications", ct);

    public Task<IReadOnlyList<PermissionResponse>> ListPermissionsAsync(Guid appId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<PermissionResponse>>($"api/v1/admin/applications/{appId}/permissions", ct);

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        return await ReadAsync<T>(response, ct);
    }

    /// <summary>A query string of the non-null values; times are sent as UTC ISO 8601.</summary>
    private static string Query(params (string Name, object? Value)[] values)
    {
        var parts = values.Where(v => v.Value is not null && v.Value is not "").Select(v => $"{v.Name}={Uri.EscapeDataString(v.Value switch
        {
            DateTime d => d.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            var other => other!.ToString()!,
        })}").ToList();
        return parts.Count == 0 ? "" : "?" + string.Join('&', parts);
    }

    private async Task PostAsync<T>(string path, T body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, body, Json, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        string? type = null, title = null, detail = null;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            type = problem.TryGetProperty("type", out var t) ? t.GetString() : null;
            title = problem.TryGetProperty("title", out var ti) ? ti.GetString() : null;
            detail = problem.TryGetProperty("detail", out var d) ? d.GetString() : null;
        }
        catch (JsonException)
        {
            // Not a problem-details body (e.g. a proxy error page).
        }
        throw new BrokerApiException(response.StatusCode, type, title, detail);
    }
}

public static class BrokerClientExtensions
{
    /// <summary>Registers <see cref="BrokerClient"/> as a typed HttpClient with the base address and API key.</summary>
    public static IHttpClientBuilder AddBrokerClient(this IServiceCollection services, Action<BrokerClientOptions> configure)
    {
        var options = new BrokerClientOptions();
        configure(options);
        if (options.BaseAddress is null || string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("BaseAddress and ApiKey are required.", nameof(configure));

        return services.AddHttpClient<BrokerClient>(http =>
        {
            http.BaseAddress = options.BaseAddress;
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("ApiKey", options.ApiKey);
            // Long-polling receives wait up to 30 s on the server.
            http.Timeout = TimeSpan.FromSeconds(60);
        });
    }
}
