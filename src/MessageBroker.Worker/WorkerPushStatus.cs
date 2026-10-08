using MessageBroker.Application.Dispatch;
using MessageBroker.Worker.Webhooks;

namespace MessageBroker.Worker;

/// <summary>The dispatcher's SignalR connections and webhook circuits, for the admin overview.</summary>
public sealed class WorkerPushStatus(IConnectionRegistry connections, CircuitBreakerRegistry circuits) : IPushStatus
{
    public int ConnectedClients(Guid subscriptionId) => connections.Count(subscriptionId);

    public string CircuitState(Guid subscriptionId) => circuits.GetState(subscriptionId).ToString();
}
