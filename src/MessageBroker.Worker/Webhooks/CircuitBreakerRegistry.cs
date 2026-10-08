using System.Collections.Concurrent;
using MessageBroker.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MessageBroker.Worker.Webhooks;

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen,
}

/// <summary>
/// One circuit per webhook subscription (spec section 10). After CircuitFailureThreshold (5)
/// consecutive failed calls it opens and nothing is leased, so waiting deliveries use no attempts.
/// After CircuitOpenSeconds (60) it goes half-open and allows one trial delivery: success closes it,
/// failure opens it again. In memory, so per process ([Fix 11]).
/// </summary>
public sealed class CircuitBreakerRegistry(IOptions<BrokerOptions> options, TimeProvider time, ILogger<CircuitBreakerRegistry> logger)
{
    private sealed class Circuit
    {
        public CircuitState State;
        public int ConsecutiveFailures;
        public DateTimeOffset OpenedAt;
        public bool TrialInFlight;
    }

    private readonly ConcurrentDictionary<Guid, Circuit> _circuits = new();

    public CircuitState GetState(Guid subscriptionId)
    {
        var circuit = Get(subscriptionId);
        lock (circuit)
        {
            Refresh(subscriptionId, circuit);
            return circuit.State;
        }
    }

    /// <summary>How many deliveries may start now: unlimited when closed, one trial when half-open, none when open.</summary>
    public int Allowance(Guid subscriptionId)
    {
        var circuit = Get(subscriptionId);
        lock (circuit)
        {
            Refresh(subscriptionId, circuit);
            return circuit.State switch
            {
                CircuitState.Closed => int.MaxValue,
                CircuitState.HalfOpen => circuit.TrialInFlight ? 0 : 1,
                _ => 0,
            };
        }
    }

    /// <summary>Call when a delivery starts; in half-open state it becomes the single trial.</summary>
    public void OnAttemptStarted(Guid subscriptionId)
    {
        var circuit = Get(subscriptionId);
        lock (circuit)
        {
            Refresh(subscriptionId, circuit);
            if (circuit.State == CircuitState.HalfOpen)
                circuit.TrialInFlight = true;
        }
    }

    public void RecordSuccess(Guid subscriptionId)
    {
        var circuit = Get(subscriptionId);
        lock (circuit)
        {
            if (circuit.State != CircuitState.Closed)
                logger.LogInformation("Circuit for subscription {SubscriptionId} closed after a successful trial delivery", subscriptionId);
            circuit.State = CircuitState.Closed;
            circuit.ConsecutiveFailures = 0;
            circuit.TrialInFlight = false;
        }
    }

    public void RecordFailure(Guid subscriptionId)
    {
        var webhooks = options.Value.Webhooks;
        var circuit = Get(subscriptionId);
        lock (circuit)
        {
            Refresh(subscriptionId, circuit);
            switch (circuit.State)
            {
                case CircuitState.Closed:
                    if (++circuit.ConsecutiveFailures >= webhooks.CircuitFailureThreshold)
                    {
                        Open(circuit);
                        logger.LogWarning("Circuit for subscription {SubscriptionId} opened after {Failures} consecutive failures; pausing for {Seconds} s",
                            subscriptionId, circuit.ConsecutiveFailures, webhooks.CircuitOpenSeconds);
                    }
                    break;
                case CircuitState.HalfOpen:
                    Open(circuit);
                    logger.LogWarning("Circuit for subscription {SubscriptionId} reopened: the trial delivery failed", subscriptionId);
                    break;
                // Open: calls that were already in flight when it opened change nothing.
            }
        }
    }

    private Circuit Get(Guid subscriptionId) => _circuits.GetOrAdd(subscriptionId, _ => new Circuit());

    private void Open(Circuit circuit)
    {
        circuit.State = CircuitState.Open;
        circuit.OpenedAt = time.GetUtcNow();
        circuit.TrialInFlight = false;
    }

    private void Refresh(Guid subscriptionId, Circuit circuit)
    {
        if (circuit.State == CircuitState.Open
            && time.GetUtcNow() - circuit.OpenedAt >= TimeSpan.FromSeconds(options.Value.Webhooks.CircuitOpenSeconds))
        {
            circuit.State = CircuitState.HalfOpen;
            circuit.TrialInFlight = false;
            logger.LogInformation("Circuit for subscription {SubscriptionId} half-open: allowing one trial delivery", subscriptionId);
        }
    }
}
