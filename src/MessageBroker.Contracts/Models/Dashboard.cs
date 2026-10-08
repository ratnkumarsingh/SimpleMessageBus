namespace MessageBroker.Contracts.Models;

// Admin dashboard: GET /api/v1/admin/overview, /admin/messages and /admin/deadletters, and the admin hub.

public sealed record OverviewResponse
{
    public OverviewTotals Totals { get; init; } = new();
    /// <summary>One point per minute, oldest first, covering <see cref="OverviewTotals.WindowMinutes"/>.</summary>
    public IReadOnlyList<ThroughputPoint> Series { get; init; } = [];
    public IReadOnlyList<SubscriptionHealth> Subscriptions { get; init; } = [];
    /// <summary>The newest dispatcher heartbeat; null when none was ever written.</summary>
    public DateTime? DispatcherLastBeatAt { get; init; }
    public double? DispatcherHeartbeatAgeSeconds { get; init; }
}

public sealed record OverviewTotals
{
    public long Pending { get; init; }
    public long Leased { get; init; }
    /// <summary>Open DLQ entries (not requeued).</summary>
    public long DeadLettered { get; init; }
    public long PublishedInWindow { get; init; }
    public long CompletedInWindow { get; init; }
    public int WindowMinutes { get; init; }
    public DateTime GeneratedAt { get; init; }
}

public sealed record ThroughputPoint(DateTime Minute, int Published, int Completed, int Failed, int DeadLettered);

/// <summary>A subscription's backlog and push state. No webhook URL or secrets.</summary>
public sealed record SubscriptionHealth
{
    public Guid SubscriptionId { get; init; }
    public string Name { get; init; } = "";
    public Guid TopicId { get; init; }
    public string TopicName { get; init; } = "";
    public Guid OwnerAppId { get; init; }
    /// <summary>Active or Paused.</summary>
    public string Status { get; init; } = "";
    public string DeliveryMode { get; init; } = "";
    public int Pending { get; init; }
    public int Leased { get; init; }
    public int DeadLettered { get; init; }
    /// <summary>SignalR subscriptions: connected clients on this broker instance.</summary>
    public int? ConnectedClients { get; init; }
    /// <summary>Webhook subscriptions: Closed, Open or HalfOpen on this broker instance.</summary>
    public string? CircuitState { get; init; }
}

/// <summary>Query of GET /api/v1/admin/messages. Every filter is optional; times are UTC.</summary>
public sealed record MessageSearchRequest
{
    public Guid? TopicId { get; init; }
    /// <summary>InProgress, Completed, PartiallyDeadLettered or DeadLettered.</summary>
    public string? Status { get; init; }
    public string? MessageType { get; init; }
    public string? CorrelationId { get; init; }
    public Guid? PublisherAppId { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    /// <summary>The previous page's <see cref="MessageSearchResponse.NextCursor"/>.</summary>
    public long? Cursor { get; init; }
    /// <summary>1–200; default 50.</summary>
    public int? PageSize { get; init; }
}

/// <summary>Newest first. <see cref="NextCursor"/> is null on the last page.</summary>
public sealed record MessageSearchResponse(IReadOnlyList<MessageListItem> Items, long? NextCursor);

public sealed record MessageListItem
{
    public Guid MessageId { get; init; }
    public string TopicName { get; init; } = "";
    public string MessageType { get; init; } = "";
    public string CorrelationId { get; init; } = "";
    public Guid PublisherAppId { get; init; }
    public string PublisherName { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public string Status { get; init; } = "";
    public int DeliveryCount { get; init; }
    public int Pending { get; init; }
    public int Leased { get; init; }
    public int Completed { get; init; }
    public int DeadLettered { get; init; }
}

/// <summary>Query of GET /api/v1/admin/deadletters. Every filter is optional; times are UTC.</summary>
public sealed record DeadLetterSearchRequest
{
    public Guid? TopicId { get; init; }
    public Guid? SubscriptionId { get; init; }
    /// <summary>MaxAttemptsExceeded, Expired or RejectedBySubscriber.</summary>
    public string? Reason { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public bool IncludeRequeued { get; init; }
    /// <summary>The previous page's <see cref="DeadLetterPage.NextBefore"/>.</summary>
    public long? Before { get; init; }
    /// <summary>1–200; default 50.</summary>
    public int? PageSize { get; init; }
}

/// <summary>One change pushed on the admin hub. <see cref="Kind"/> is one of <see cref="ActivityKinds"/>.</summary>
public sealed record MessageActivity(string Kind, Guid MessageId, long? DeliveryId, DateTime At);
