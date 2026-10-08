namespace MessageBroker.Contracts;

/// <summary>
/// The admin hub contract: live activity for dashboards. Admin keys only; the hub sends, clients
/// only listen.
/// </summary>
public static class AdminHub
{
    public const string Path = "/hubs/admin";

    /// <summary>Hub to client: a batch (array) of <see cref="Models.MessageActivity"/>, sent every 250 ms while there is any.</summary>
    public const string Activity = "Activity";

    /// <summary>Hub to client, no arguments: counts changed; sent at most once a second. Re-read the overview.</summary>
    public const string OverviewChanged = "OverviewChanged";
}

/// <summary>Values of <see cref="Models.MessageActivity.Kind"/>.</summary>
public static class ActivityKinds
{
    public const string Published = "Published";
    /// <summary>A delivery of the message was leased, settled, expired or requeued.</summary>
    public const string DeliveryChanged = "DeliveryChanged";
}
