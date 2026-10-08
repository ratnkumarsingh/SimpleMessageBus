namespace MessageBroker.Domain;

/// <summary>Delivery state. Values match the tinyint stored in broker.Deliveries.Status.</summary>
public enum DeliveryStatus : byte
{
    Pending = 0,
    Leased = 1,
    Completed = 2,
    DeadLettered = 3,
    Cancelled = 4,
}

public enum DeliveryMode
{
    Webhook,
    SignalR,
    Pull,
}

public enum SubscriptionStatus
{
    Active,
    Paused,
    Deleted,
}

public enum DeliveryChannel
{
    Webhook,
    SignalR,
    Pull,
}

public enum AttemptOutcome
{
    Acked,
    Nacked,
    LeaseExpired,
    /// <summary>The subscription was deleted while the attempt was open.</summary>
    Cancelled,
}

public enum DeadLetterReason
{
    MaxAttemptsExceeded,
    Expired,
    RejectedBySubscriber,
}

/// <summary>Derived from a message's deliveries; never stored.</summary>
public enum MessageStatus
{
    InProgress,
    Completed,
    PartiallyDeadLettered,
    DeadLettered,
}

public enum ResourceType
{
    Topic,
    Subscription,
}

public enum Permission
{
    Publish,
    Receive,
    Manage,
}
