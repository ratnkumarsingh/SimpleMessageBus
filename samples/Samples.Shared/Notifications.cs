using System.Text.Json;
using MessageBroker.Contracts.Client;
using MessageBroker.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace Samples.Shared;

/// <summary>The payload the Blazor samples exchange on the notifications topic.</summary>
public sealed record UserNotification(string Title, string Text, NotificationLevel Level, string SentBy);

public enum NotificationLevel { Info, Success, Warning, Error }

/// <summary>A notification as the subscriber received it.</summary>
public sealed record ReceivedNotification(Guid MessageId, UserNotification Notification, DateTime PublishedAt, DateTime ReceivedAt);

public static class NotificationMessages
{
    public const string MessageType = "UserNotification";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static PublishRequest ToPublishRequest(UserNotification notification) => new()
    {
        MessageType = MessageType,
        Payload = JsonSerializer.SerializeToElement(notification, Json),
    };
}

/// <summary>
/// In-memory list of received notifications. Blazor components subscribe to <see cref="Received"/> to
/// show a toast; the event is raised on the broker listener's thread, so handlers use InvokeAsync.
/// </summary>
public sealed class NotificationFeed
{
    private const int Capacity = 50;
    private readonly Lock _lock = new();
    private readonly LinkedList<ReceivedNotification> _items = new();
    private readonly HashSet<Guid> _seen = [];

    public event Action<ReceivedNotification>? Received;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ReceivedNotification> Items
    {
        get { lock (_lock) return [.. _items]; }
    }

    /// <summary>Adds the notification unless its message was already received; true when added.</summary>
    public bool TryAdd(ReceivedNotification item)
    {
        lock (_lock)
        {
            if (!_seen.Add(item.MessageId))
                return false;
            _items.AddFirst(item);
            if (_items.Count > Capacity)
            {
                _seen.Remove(_items.Last!.Value.MessageId);
                _items.RemoveLast();
            }
        }
        Received?.Invoke(item);
        return true;
    }
}

/// <summary>
/// Handles deliveries on the notifications subscription. A redelivered message (delivery is
/// at-least-once) is ACKed without a second toast; a payload that cannot be read is dead-lettered,
/// since retrying it cannot help.
/// </summary>
public sealed class NotificationHandler(NotificationFeed feed, TimeProvider time, ILogger<NotificationHandler> logger)
{
    public Task<DeliveryResult> HandleAsync(Delivery delivery, CancellationToken ct)
    {
        var message = delivery.Message;
        UserNotification? notification = null;
        try
        {
            if (message.MessageType == NotificationMessages.MessageType)
                notification = message.Payload.Deserialize<UserNotification>(NotificationMessages.Json);
        }
        catch (JsonException)
        {
        }

        if (notification is null || string.IsNullOrWhiteSpace(notification.Title))
        {
            logger.LogWarning("Message {MessageId} is not a valid {MessageType}; dead-lettering it", message.MessageId, NotificationMessages.MessageType);
            return Task.FromResult(DeliveryResult.Fail("InvalidNotification", "Payload is not a UserNotification with a title.", deadLetter: true));
        }

        var added = feed.TryAdd(new ReceivedNotification(message.MessageId, notification, message.CreatedAt, time.GetUtcNow().UtcDateTime));
        logger.LogInformation(added ? "Notification {MessageId} received" : "Notification {MessageId} was already shown; ACKing the redelivery",
            message.MessageId);
        return Task.FromResult(DeliveryResult.Ack);
    }
}
