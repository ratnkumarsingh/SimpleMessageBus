namespace MessageBroker.Application.Dispatch;

/// <summary>
/// [Fix 10] The dispatcher's in-memory push state, for the admin overview. Implemented by the
/// Worker; per process ([Fix 11]).
/// </summary>
public interface IPushStatus
{
    /// <summary>SignalR clients connected for the subscription.</summary>
    int ConnectedClients(Guid subscriptionId);

    /// <summary>The webhook circuit: Closed, Open or HalfOpen.</summary>
    string CircuitState(Guid subscriptionId);
}

/// <summary>The default when the host runs no dispatcher.</summary>
public sealed class NullPushStatus : IPushStatus
{
    public int ConnectedClients(Guid subscriptionId) => 0;
    public string CircuitState(Guid subscriptionId) => "Closed";
}
