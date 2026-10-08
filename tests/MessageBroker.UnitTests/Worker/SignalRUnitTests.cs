using MessageBroker.Application.Dispatch;
using MessageBroker.Worker.SignalR;
using Microsoft.Extensions.Time.Testing;

namespace MessageBroker.UnitTests.Worker;

/// <summary>U11 — connection registry.</summary>
public class ConnectionRegistryTests
{
    private readonly ConnectionRegistry _registry = new();
    private readonly Guid _sub = Guid.NewGuid();

    [Fact]
    public void Round_robin_cycles_through_connections_in_join_order()
    {
        _registry.Add(_sub, "a");
        _registry.Add(_sub, "b");
        _registry.Add(_sub, "c");

        Assert.Equal(["a", "b", "c", "a", "b"], Enumerable.Range(0, 5).Select(_ => _registry.Next(_sub)));
        Assert.Equal(3, _registry.Count(_sub));
    }

    [Fact]
    public void Joining_twice_does_not_double_the_share()
    {
        _registry.Add(_sub, "a");
        _registry.Add(_sub, "a");
        _registry.Add(_sub, "b");
        Assert.Equal(["a", "b", "a", "b"], Enumerable.Range(0, 4).Select(_ => _registry.Next(_sub)));
    }

    [Fact]
    public void No_connections_means_no_next()
    {
        Assert.Null(_registry.Next(_sub));
        Assert.Equal(0, _registry.Count(_sub));
    }

    [Fact]
    public void Disconnect_removes_the_connection_from_every_subscription()
    {
        var other = Guid.NewGuid();
        _registry.Add(_sub, "a");
        _registry.Add(_sub, "b");
        _registry.Add(other, "a");

        _registry.RemoveConnection("a");

        Assert.Equal("b", _registry.Next(_sub));
        Assert.Equal("b", _registry.Next(_sub));
        Assert.Equal(0, _registry.Count(other));
        Assert.Null(_registry.Next(other));
        _registry.RemoveConnection("unknown"); // no-op
    }

    [Fact]
    public void Unsubscribe_leaves_other_subscriptions_of_the_connection()
    {
        var other = Guid.NewGuid();
        _registry.Add(_sub, "a");
        _registry.Add(other, "a");

        Assert.True(_registry.Remove(_sub, "a"));
        Assert.False(_registry.Remove(_sub, "a"));

        Assert.Equal(0, _registry.Count(_sub));
        Assert.Equal("a", _registry.Next(other));
    }

    [Fact]
    public void Removing_a_connection_keeps_the_rotation_fair()
    {
        _registry.Add(_sub, "a");
        _registry.Add(_sub, "b");
        _registry.Add(_sub, "c");
        Assert.Equal("a", _registry.Next(_sub));
        Assert.Equal("b", _registry.Next(_sub));

        _registry.Remove(_sub, "a"); // next would have been c

        Assert.Equal(["c", "b", "c"], Enumerable.Range(0, 3).Select(_ => _registry.Next(_sub)));
    }

    [Fact]
    public async Task Concurrent_use_is_safe()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 1000; n++)
            {
                _registry.Add(_sub, $"c{i}");
                _registry.Next(_sub);
                _registry.Remove(_sub, $"c{i}");
            }
        })));
        Assert.Equal(0, _registry.Count(_sub));
    }
}

/// <summary>Settlement tracking used by the SignalR channel.</summary>
public class DeliverySettlementsTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 8, 5, 0, 0, TimeSpan.Zero));
    private readonly DeliverySettlements _settlements = new();

    private DateTime In(int seconds) => _time.GetUtcNow().UtcDateTime.AddSeconds(seconds);

    [Fact]
    public async Task Settlement_completes_the_wait()
    {
        using var waiter = _settlements.Track(1, In(60));
        var waiting = waiter.WaitAsync(_time, CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        _settlements.Settled(1);
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Settlement_before_the_wait_is_not_lost()
    {
        using var waiter = _settlements.Track(1, In(60));
        _settlements.Settled(1);
        Assert.True(await waiter.WaitAsync(_time, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Lease_end_completes_the_wait_unsettled()
    {
        using var waiter = _settlements.Track(1, In(60));
        var waiting = waiter.WaitAsync(_time, CancellationToken.None);

        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Renewal_extends_the_wait()
    {
        using var waiter = _settlements.Track(1, In(60));
        var waiting = waiter.WaitAsync(_time, CancellationToken.None);

        _time.Advance(TimeSpan.FromSeconds(50));
        _settlements.Renewed(1, In(60)); // now + 60
        _time.Advance(TimeSpan.FromSeconds(10)); // original lease end
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);

        _settlements.Settled(1);
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Disposed_waiters_are_forgotten()
    {
        var waiter = _settlements.Track(1, In(60));
        waiter.Dispose();
        _settlements.Settled(1); // no-op
        _settlements.Renewed(1, In(120));
        Assert.Equal(In(60), waiter.LockedUntil);
    }
}
