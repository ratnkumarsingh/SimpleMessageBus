using MessageBroker.Application.Dispatch;
using MessageBroker.Application.Persistence;
using MessageBroker.Application.Security;
using MessageBroker.Contracts.Models;
using MessageBroker.Domain;

namespace MessageBroker.Application.Services;

/// <summary>Read models for the admin dashboard: overview, message search and the broker-wide DLQ. Admin only.</summary>
public sealed class DashboardService(IDashboardRepository dashboard, IPushStatus push)
{
    public const int DefaultWindowMinutes = 60;
    public const int MinWindowMinutes = 5;
    public const int MaxWindowMinutes = 1440;
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public async Task<OverviewResponse> GetOverviewAsync(Caller caller, int? windowMinutes, CancellationToken ct)
    {
        caller.RequireAdmin();
        var window = windowMinutes ?? DefaultWindowMinutes;
        if (window is < MinWindowMinutes or > MaxWindowMinutes)
            throw new BrokerValidationException([new("windowMinutes", $"windowMinutes must be between {MinWindowMinutes} and {MaxWindowMinutes}.")]);

        var o = await dashboard.GetOverviewAsync(window, ct);
        return new OverviewResponse
        {
            Totals = new OverviewTotals
            {
                Pending = o.Totals.PendingCount,
                Leased = o.Totals.LeasedCount,
                DeadLettered = o.Totals.DeadLetteredCount,
                PublishedInWindow = o.Totals.PublishedInWindow,
                CompletedInWindow = o.Totals.CompletedInWindow,
                WindowMinutes = o.Totals.WindowMinutes,
                GeneratedAt = o.Totals.GeneratedAt,
            },
            Series = o.Series.Select(p => new ThroughputPoint(p.Minute, p.Published, p.Completed, p.Failed, p.DeadLettered)).ToList(),
            Subscriptions = o.Subscriptions.Select(s => new SubscriptionHealth
            {
                SubscriptionId = s.SubscriptionId,
                Name = s.Name,
                TopicId = s.TopicId,
                TopicName = s.TopicName,
                OwnerAppId = s.OwnerAppId,
                Status = s.Status,
                DeliveryMode = s.DeliveryMode,
                Pending = s.PendingCount,
                Leased = s.LeasedCount,
                DeadLettered = s.DeadLetteredCount,
                ConnectedClients = s.DeliveryMode == nameof(DeliveryMode.SignalR) ? push.ConnectedClients(s.SubscriptionId) : null,
                CircuitState = s.DeliveryMode == nameof(DeliveryMode.Webhook) ? push.CircuitState(s.SubscriptionId) : null,
            }).ToList(),
            DispatcherLastBeatAt = o.Heartbeat.LastBeatAt,
            DispatcherHeartbeatAgeSeconds = o.Heartbeat.LastBeatAt is { } beat ? Math.Max(0, (o.Heartbeat.DbNow - beat).TotalSeconds) : null,
        };
    }

    public async Task<MessageSearchResponse> SearchMessagesAsync(Caller caller, MessageSearchRequest request, CancellationToken ct)
    {
        caller.RequireAdmin();
        var errors = new List<ValidationError>();
        var size = PageSize(request.PageSize, errors);
        string? status = null;
        if (request.Status is { Length: > 0 })
        {
            if (EnumNames.TryParse<MessageStatus>(request.Status, out var parsed))
                status = parsed.ToString();
            else
                errors.Add(new("status", "status must be InProgress, Completed, PartiallyDeadLettered or DeadLettered."));
        }
        var (from, to) = Range(request.From, request.To, errors);
        if (errors.Count > 0)
            throw new BrokerValidationException(errors);

        var rows = await dashboard.SearchMessagesAsync(new MessageSearchQuery(
            request.TopicId, status, Blank(request.MessageType), Blank(request.CorrelationId), request.PublisherAppId,
            from, to, request.Cursor, size), ct);

        var items = rows.Select(m => new MessageListItem
        {
            MessageId = m.MessageId,
            TopicName = m.TopicName,
            MessageType = m.MessageType,
            CorrelationId = m.CorrelationId,
            PublisherAppId = m.PublisherAppId,
            PublisherName = m.PublisherName,
            CreatedAt = m.CreatedAt,
            ExpiresAt = m.ExpiresAt,
            Status = m.Status,
            DeliveryCount = m.DeliveryCount,
            Pending = m.PendingCount,
            Leased = m.LeasedCount,
            Completed = m.CompletedCount,
            DeadLettered = m.DeadLetteredCount,
        }).ToList();
        // A full page may be followed by an empty one; that costs one cheap query and saves an extra row on every page.
        return new MessageSearchResponse(items, rows.Count == size ? rows[^1].MessageSeq : null);
    }

    public async Task<DeadLetterPage> SearchDeadLettersAsync(Caller caller, DeadLetterSearchRequest request, CancellationToken ct)
    {
        caller.RequireAdmin();
        var errors = new List<ValidationError>();
        var size = PageSize(request.PageSize, errors);
        string? reason = null;
        if (request.Reason is { Length: > 0 })
        {
            if (EnumNames.TryParse<DeadLetterReason>(request.Reason, out var parsed))
                reason = parsed.ToString();
            else
                errors.Add(new("reason", "reason must be MaxAttemptsExceeded, Expired or RejectedBySubscriber."));
        }
        var (from, to) = Range(request.From, request.To, errors);
        if (errors.Count > 0)
            throw new BrokerValidationException(errors);

        var rows = await dashboard.SearchDeadLettersAsync(new DeadLetterSearchQuery(
            request.TopicId, request.SubscriptionId, reason, from, to, request.IncludeRequeued, request.Before, size), ct);
        var items = rows.Select(r => r.ToResponse()).ToList();
        return new DeadLetterPage(items, rows.Count == size ? rows[^1].DeadLetterId : null);
    }

    private static int PageSize(int? requested, List<ValidationError> errors)
    {
        var size = requested ?? DefaultPageSize;
        if (size is < 1 or > MaxPageSize)
            errors.Add(new("pageSize", $"pageSize must be between 1 and {MaxPageSize}."));
        return size;
    }

    private static (DateTime? From, DateTime? To) Range(DateTime? from, DateTime? to, List<ValidationError> errors)
    {
        var (f, t) = (Utc(from), Utc(to));
        if (f > t)
            errors.Add(new("from", "from must not be later than to."));
        return (f, t);
    }

    /// <summary>Query-string times bind as local when they carry an offset; unmarked times are taken as UTC.</summary>
    private static DateTime? Utc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Local } v => v.ToUniversalTime(),
        { Kind: DateTimeKind.Unspecified } v => DateTime.SpecifyKind(v, DateTimeKind.Utc),
        var v => v,
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
