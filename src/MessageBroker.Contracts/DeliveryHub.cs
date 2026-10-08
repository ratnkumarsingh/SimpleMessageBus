namespace MessageBroker.Contracts;

/// <summary>The SignalR hub contract (spec section 9).</summary>
public static class DeliveryHub
{
    public const string Path = "/hubs/deliveries";

    /// <summary>Hub to client: one <see cref="Models.Delivery"/>.</summary>
    public const string Deliver = "Deliver";

    // Client to hub.
    public const string Subscribe = "Subscribe";
    public const string Unsubscribe = "Unsubscribe";
    public const string Ack = "Ack";
    public const string Nack = "Nack";
    public const string Renew = "Renew";
}
