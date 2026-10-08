using System.Collections.Concurrent;
using System.Text.Json;
using MessageBroker.Contracts.Models;
using MessageBroker.Dashboard;
using MessageBroker.Dashboard.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MessageBroker.Dashboard.Tests;

/// <summary>An in-memory broker for page tests. Records what the pages asked for.</summary>
public sealed class FakeBrokerAdminClient : IBrokerAdminClient
{
    public OverviewResponse Overview { get; set; } = new();
    public Func<MessageSearchRequest, MessageSearchResponse> Search { get; set; } = _ => new([], null);
    public Dictionary<Guid, MessageResponse> MessagesById { get; } = [];
    public List<DeadLetterResponse> DeadLetters { get; } = [];
    public HashSet<long> FailingRequeues { get; } = [];
    public List<TopicResponse> Topics { get; } = [];
    public Dictionary<Guid, List<SubscriptionResponse>> SubscriptionsByTopic { get; } = [];
    public List<ApplicationResponse> Applications { get; } = [];

    public ConcurrentQueue<string> Calls { get; } = new();
    public ConcurrentQueue<MessageSearchRequest> Searches { get; } = new();
    public ConcurrentQueue<long> Requeued { get; } = new();

    public int CallCount(string name) => Calls.Count(c => c == name);

    public Task<OverviewResponse> GetOverviewAsync(int windowMinutes, CancellationToken ct = default)
    {
        Calls.Enqueue(nameof(GetOverviewAsync));
        return Task.FromResult(Overview);
    }

    public Task<MessageSearchResponse> SearchMessagesAsync(MessageSearchRequest request, CancellationToken ct = default)
    {
        Calls.Enqueue(nameof(SearchMessagesAsync));
        Searches.Enqueue(request);
        return Task.FromResult(Search(request));
    }

    public Task<MessageResponse> GetMessageAsync(Guid messageId, CancellationToken ct = default)
    {
        Calls.Enqueue(nameof(GetMessageAsync));
        return MessagesById.TryGetValue(messageId, out var m)
            ? Task.FromResult(m)
            : Task.FromException<MessageResponse>(new Contracts.Client.BrokerApiException(System.Net.HttpStatusCode.NotFound, null, "Not found", null));
    }

    public Task<IReadOnlyList<MessageSummaryResponse>> ListMessagesByCorrelationAsync(string correlationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MessageSummaryResponse>>([]);

    public Task<DeadLetterPage> SearchDeadLettersAsync(DeadLetterSearchRequest request, CancellationToken ct = default)
    {
        Calls.Enqueue(nameof(SearchDeadLettersAsync));
        var rows = DeadLetters.Where(d => request.IncludeRequeued || d.RequeuedAt is null).ToList();
        return Task.FromResult(new DeadLetterPage(rows, null));
    }

    public Task RequeueDeadLetterAsync(long deliveryId, CancellationToken ct = default)
    {
        Requeued.Enqueue(deliveryId);
        if (FailingRequeues.Contains(deliveryId))
            return Task.FromException(new Contracts.Client.BrokerApiException(System.Net.HttpStatusCode.Conflict, null, "Conflict", null));
        var index = DeadLetters.FindIndex(d => d.DeliveryId == deliveryId);
        DeadLetters[index] = DeadLetters[index] with { RequeuedAt = DateTime.UtcNow };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TopicResponse>> ListTopicsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TopicResponse>>(Topics);

    public Task<IReadOnlyList<SubscriptionResponse>> ListSubscriptionsAsync(Guid topicId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SubscriptionResponse>>(SubscriptionsByTopic.GetValueOrDefault(topicId) ?? []);

    public Task<IReadOnlyList<ApplicationResponse>> ListApplicationsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ApplicationResponse>>(Applications);

    public Task<IReadOnlyList<PermissionResponse>> ListPermissionsAsync(Guid appId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PermissionResponse>>([]);
}

/// <summary>A live feed the test drives by hand.</summary>
public sealed class FakeLiveFeed : ILiveFeed
{
    public LiveState State { get; set; } = LiveState.Live;
    public event Action<IReadOnlyList<MessageActivity>>? Activity;
    public event Action? OverviewChanged;
    public event Action? StateChanged;

    public Task StartAsync() => Task.CompletedTask;

    public void RaiseActivity(params MessageActivity[] batch) => Activity?.Invoke(batch);
    public void RaiseOverviewChanged() => OverviewChanged?.Invoke();

    public void SetState(LiveState state)
    {
        State = state;
        StateChanged?.Invoke();
    }
}

/// <summary>Base for page tests: fakes for the broker and the live feed, no refresh delay, loose JS interop.</summary>
public abstract class PageTest : BunitContext
{
    protected FakeBrokerAdminClient Broker { get; } = new();
    protected FakeLiveFeed Live { get; } = new();

    protected PageTest()
    {
        Services.AddSingleton<IBrokerAdminClient>(Broker);
        Services.AddSingleton<ILiveFeed>(Live);
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton(Options.Create(new DashboardOptions { RefreshThrottleMs = 0 }));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    protected static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
