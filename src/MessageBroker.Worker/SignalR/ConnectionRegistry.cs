using MessageBroker.Application.Dispatch;

namespace MessageBroker.Worker.SignalR;

/// <summary>
/// In-memory map of subscription to connected SignalR connections, with a round-robin cursor per
/// subscription. Rebuilt from Subscribe calls after a restart (spec section 8.6).
/// </summary>
public sealed class ConnectionRegistry : IConnectionRegistry
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, List<string>> _bySubscription = [];
    private readonly Dictionary<string, HashSet<Guid>> _byConnection = [];
    private readonly Dictionary<Guid, int> _cursor = [];

    public void Add(Guid subscriptionId, string connectionId)
    {
        lock (_lock)
        {
            if (!_bySubscription.TryGetValue(subscriptionId, out var connections))
                _bySubscription[subscriptionId] = connections = [];
            if (!connections.Contains(connectionId))
                connections.Add(connectionId);

            if (!_byConnection.TryGetValue(connectionId, out var subscriptions))
                _byConnection[connectionId] = subscriptions = [];
            subscriptions.Add(subscriptionId);
        }
    }

    public bool Remove(Guid subscriptionId, string connectionId)
    {
        lock (_lock)
        {
            if (_byConnection.TryGetValue(connectionId, out var subscriptions))
            {
                subscriptions.Remove(subscriptionId);
                if (subscriptions.Count == 0)
                    _byConnection.Remove(connectionId);
            }
            return RemoveFromSubscription(subscriptionId, connectionId);
        }
    }

    public void RemoveConnection(string connectionId)
    {
        lock (_lock)
        {
            if (!_byConnection.Remove(connectionId, out var subscriptions))
                return;
            foreach (var subscriptionId in subscriptions)
                RemoveFromSubscription(subscriptionId, connectionId);
        }
    }

    public int Count(Guid subscriptionId)
    {
        lock (_lock)
            return _bySubscription.TryGetValue(subscriptionId, out var connections) ? connections.Count : 0;
    }

    public string? Next(Guid subscriptionId)
    {
        lock (_lock)
        {
            if (!_bySubscription.TryGetValue(subscriptionId, out var connections) || connections.Count == 0)
                return null;
            var index = _cursor.GetValueOrDefault(subscriptionId) % connections.Count;
            _cursor[subscriptionId] = index + 1;
            return connections[index];
        }
    }

    private bool RemoveFromSubscription(Guid subscriptionId, string connectionId)
    {
        if (!_bySubscription.TryGetValue(subscriptionId, out var connections))
            return false;

        var index = connections.IndexOf(connectionId);
        if (index < 0)
            return false;
        connections.RemoveAt(index);

        // Keep the rotation fair: connections after the removed one shift down by one.
        var cursor = _cursor.GetValueOrDefault(subscriptionId);
        if (index < cursor)
            _cursor[subscriptionId] = cursor - 1;
        if (connections.Count == 0)
        {
            _bySubscription.Remove(subscriptionId);
            _cursor.Remove(subscriptionId);
        }
        return true;
    }
}
