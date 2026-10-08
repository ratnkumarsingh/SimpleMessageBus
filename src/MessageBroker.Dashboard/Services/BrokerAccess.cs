using System.Security.Claims;
using MessageBroker.Contracts.Client;
using MessageBroker.Contracts.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;

namespace MessageBroker.Dashboard.Services;

public static class DashboardClaims
{
    /// <summary>The operator's admin API key, kept only inside the encrypted sign-in cookie.</summary>
    public const string ApiKey = "dashboard:api_key";
    public const string KeyPrefix = "dashboard:key_prefix";

    /// <summary>The public part of a key ("mbk_abc123def456"), safe to show and log.</summary>
    public static string PrefixOf(string apiKey)
    {
        var second = apiKey.IndexOf('_', apiKey.IndexOf('_') + 1);
        return second > 0 ? apiKey[..second] : "key";
    }
}

/// <summary>What the pages read from and do to the broker. Everything runs with the signed-in operator's key.</summary>
public interface IBrokerAdminClient
{
    Task<OverviewResponse> GetOverviewAsync(int windowMinutes, CancellationToken ct = default);
    Task<MessageSearchResponse> SearchMessagesAsync(MessageSearchRequest request, CancellationToken ct = default);
    Task<MessageResponse> GetMessageAsync(Guid messageId, CancellationToken ct = default);
    Task<IReadOnlyList<MessageSummaryResponse>> ListMessagesByCorrelationAsync(string correlationId, CancellationToken ct = default);
    Task<DeadLetterPage> SearchDeadLettersAsync(DeadLetterSearchRequest request, CancellationToken ct = default);
    Task RequeueDeadLetterAsync(long deliveryId, CancellationToken ct = default);
    Task<IReadOnlyList<TopicResponse>> ListTopicsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SubscriptionResponse>> ListSubscriptionsAsync(Guid topicId, CancellationToken ct = default);
    Task<IReadOnlyList<ApplicationResponse>> ListApplicationsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PermissionResponse>> ListPermissionsAsync(Guid appId, CancellationToken ct = default);
}

/// <summary>Creates <see cref="BrokerClient"/>s for a given key over the "broker" HttpClient.</summary>
public sealed class BrokerClientFactory(IHttpClientFactory httpClients)
{
    public const string HttpClientName = "broker";

    public BrokerClient Create(string apiKey) => BrokerClient.Create(httpClients.CreateClient(HttpClientName), apiKey);
}

/// <summary>Checks a key at sign-in: the broker must accept it as an Admin key.</summary>
public sealed class AdminKeyValidator(BrokerClientFactory clients, ILogger<AdminKeyValidator> logger)
{
    public enum Result { Valid, NotAdmin, Invalid, BrokerUnavailable }

    public async Task<Result> ValidateAsync(string apiKey, CancellationToken ct)
    {
        try
        {
            await clients.Create(apiKey).GetOverviewAsync(DashboardDefaults.MinWindowMinutes, ct);
            return Result.Valid;
        }
        catch (BrokerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return Result.NotAdmin;
        }
        catch (BrokerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return Result.Invalid;
        }
        catch (Exception ex) when (ex is HttpRequestException or BrokerApiException or TaskCanceledException)
        {
            logger.LogWarning(ex, "The broker could not be reached to check a sign-in");
            return Result.BrokerUnavailable;
        }
    }
}

public static class DashboardDefaults
{
    public const int MinWindowMinutes = 5;
}

/// <summary>Reads the operator's key from the circuit's authentication state and calls the broker with it.</summary>
public sealed class HttpBrokerAdminClient(BrokerClientFactory clients, AuthenticationStateProvider auth) : IBrokerAdminClient
{
    private BrokerClient? _client;

    private async ValueTask<BrokerClient> ClientAsync()
    {
        if (_client is null)
        {
            var user = (await auth.GetAuthenticationStateAsync()).User;
            var key = user.FindFirstValue(DashboardClaims.ApiKey) ?? throw new InvalidOperationException("The operator is not signed in.");
            _client = clients.Create(key);
        }
        return _client;
    }

    public async Task<OverviewResponse> GetOverviewAsync(int windowMinutes, CancellationToken ct = default) =>
        await (await ClientAsync()).GetOverviewAsync(windowMinutes, ct);

    public async Task<MessageSearchResponse> SearchMessagesAsync(MessageSearchRequest request, CancellationToken ct = default) =>
        await (await ClientAsync()).SearchMessagesAsync(request, ct);

    public async Task<MessageResponse> GetMessageAsync(Guid messageId, CancellationToken ct = default) =>
        await (await ClientAsync()).GetMessageAsync(messageId, ct);

    public async Task<IReadOnlyList<MessageSummaryResponse>> ListMessagesByCorrelationAsync(string correlationId, CancellationToken ct = default) =>
        await (await ClientAsync()).ListMessagesByCorrelationAsync(correlationId, ct);

    public async Task<DeadLetterPage> SearchDeadLettersAsync(DeadLetterSearchRequest request, CancellationToken ct = default) =>
        await (await ClientAsync()).SearchDeadLettersAsync(request, ct);

    public async Task RequeueDeadLetterAsync(long deliveryId, CancellationToken ct = default) =>
        await (await ClientAsync()).RequeueDeadLetterAsync(deliveryId, ct);

    public async Task<IReadOnlyList<TopicResponse>> ListTopicsAsync(CancellationToken ct = default) =>
        await (await ClientAsync()).ListTopicsAsync(ct);

    public async Task<IReadOnlyList<SubscriptionResponse>> ListSubscriptionsAsync(Guid topicId, CancellationToken ct = default) =>
        await (await ClientAsync()).ListSubscriptionsAsync(topicId, ct);

    public async Task<IReadOnlyList<ApplicationResponse>> ListApplicationsAsync(CancellationToken ct = default) =>
        await (await ClientAsync()).ListApplicationsAsync(ct);

    public async Task<IReadOnlyList<PermissionResponse>> ListPermissionsAsync(Guid appId, CancellationToken ct = default) =>
        await (await ClientAsync()).ListPermissionsAsync(appId, ct);
}

public static class BrokerAccessExtensions
{
    public static IServiceCollection AddBrokerAccess(this IServiceCollection services)
    {
        services.AddHttpClient(BrokerClientFactory.HttpClientName, (sp, http) =>
        {
            http.BaseAddress = sp.GetRequiredService<IOptions<DashboardOptions>>().Value.BrokerUrl;
            http.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<BrokerClientFactory>();
        services.AddSingleton<AdminKeyValidator>();
        services.AddScoped<IBrokerAdminClient, HttpBrokerAdminClient>();
        return services;
    }
}
