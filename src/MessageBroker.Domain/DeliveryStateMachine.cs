namespace MessageBroker.Domain;

/// <summary>
/// The delivery lifecycle from spec section 6 (figure 4). The SQL procedures enforce these
/// transitions with WHERE clauses; this class is the documented reference they are tested against.
/// </summary>
public static class DeliveryStateMachine
{
    private static readonly Dictionary<DeliveryStatus, DeliveryStatus[]> Allowed = new()
    {
        // lease, TTL expiry, subscription deleted
        [DeliveryStatus.Pending] = [DeliveryStatus.Leased, DeliveryStatus.DeadLettered, DeliveryStatus.Cancelled],
        // ACK, NACK/lease expiry with attempts left, attempts exhausted or rejected, subscription deleted
        [DeliveryStatus.Leased] = [DeliveryStatus.Completed, DeliveryStatus.Pending, DeliveryStatus.DeadLettered, DeliveryStatus.Cancelled],
        // admin requeue only
        [DeliveryStatus.DeadLettered] = [DeliveryStatus.Pending],
        [DeliveryStatus.Completed] = [],
        [DeliveryStatus.Cancelled] = [],
    };

    public static bool CanTransition(DeliveryStatus from, DeliveryStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static bool IsTerminal(DeliveryStatus status) =>
        status is DeliveryStatus.Completed or DeliveryStatus.Cancelled;

    /// <summary>
    /// Derives a message's status from its deliveries. Cancelled deliveries are ignored, because a
    /// deleted subscription says nothing about whether the message was processed.
    /// </summary>
    public static MessageStatus DeriveMessageStatus(IEnumerable<DeliveryStatus> deliveries)
    {
        var relevant = deliveries.Where(s => s != DeliveryStatus.Cancelled).ToList();
        if (relevant.Any(s => s is DeliveryStatus.Pending or DeliveryStatus.Leased))
            return MessageStatus.InProgress;

        var deadLettered = relevant.Count(s => s == DeliveryStatus.DeadLettered);
        if (deadLettered == 0)
            return MessageStatus.Completed;
        return deadLettered == relevant.Count ? MessageStatus.DeadLettered : MessageStatus.PartiallyDeadLettered;
    }
}
