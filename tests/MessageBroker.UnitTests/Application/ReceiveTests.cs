using MessageBroker.Application.Dispatch;
using MessageBroker.Contracts.Models;
using Microsoft.Extensions.Time.Testing;

namespace MessageBroker.UnitTests.Application;

/// <summary>U10 — receive request clamping and the long-poll waiter.</summary>
public class ReceiveTests
{
    [Theory]
    [InlineData(null, null, 1, 0)]
    [InlineData(10, 20, 10, 20)]
    [InlineData(0, -1, 1, 0)]
    [InlineData(33, 31, 32, 30)]
    [InlineData(int.MaxValue, int.MinValue, 32, 0)]
    public void Clamp_keeps_values_in_range(int? max, int? wait, int expectedMax, int expectedWait)
    {
        var (maxMessages, waitTime) = ReceiveRules.Clamp(new ReceiveRequest(max, wait));
        Assert.Equal(expectedMax, maxMessages);
        Assert.Equal(TimeSpan.FromSeconds(expectedWait), waitTime);
    }

    [Fact]
    public void Clamp_handles_a_missing_body() => Assert.Equal((1, TimeSpan.Zero), ReceiveRules.Clamp(null));

    private static Func<CancellationToken, Task<IReadOnlyList<int>>> Source(Func<IReadOnlyList<int>> next, Action? onCall = null) =>
        _ => { onCall?.Invoke(); return Task.FromResult(next()); };

    [Fact]
    public async Task Returns_at_once_when_items_are_available()
    {
        var poller = new LongPoller(new DispatcherSignal(), new FakeTimeProvider());
        var calls = 0;
        var result = await poller.PollAsync(Source(() => [1, 2], () => calls++), TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal([1, 2], result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Zero_wait_tries_once()
    {
        var poller = new LongPoller(new DispatcherSignal(), new FakeTimeProvider());
        var calls = 0;
        Assert.Empty(await poller.PollAsync(Source(() => [], () => calls++), TimeSpan.Zero, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Returns_empty_at_the_deadline_retrying_every_second()
    {
        var time = new FakeTimeProvider();
        var poller = new LongPoller(new DispatcherSignal(), time);
        var calls = 0;

        var polling = poller.PollAsync(Source(() => [], () => calls++), TimeSpan.FromSeconds(3), CancellationToken.None);
        for (var i = 0; i < 3 && !polling.IsCompleted; i++)
        {
            await WaitUntilAsync(() => calls == i + 1);
            time.Advance(LongPoller.Tick);
        }

        Assert.Empty(await polling.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(4, calls); // at 0, 1, 2 and 3 seconds
    }

    [Fact]
    public async Task Signal_wakes_the_waiter_before_the_tick()
    {
        var time = new FakeTimeProvider();
        var signal = new DispatcherSignal();
        var poller = new LongPoller(signal, time);
        var calls = 0;

        var polling = poller.PollAsync(Source(() => calls >= 2 ? [42] : [], () => calls++), TimeSpan.FromSeconds(30), CancellationToken.None);
        await WaitUntilAsync(() => calls == 1);
        signal.Notify(); // no time passes

        Assert.Equal([42], await polling.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cancellation_stops_the_wait()
    {
        var poller = new LongPoller(new DispatcherSignal(), new FakeTimeProvider());
        using var cts = new CancellationTokenSource();
        var polling = poller.PollAsync(Source(() => []), TimeSpan.FromSeconds(30), cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => polling.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Signal_completes_only_waiters_present_at_notify()
    {
        var signal = new DispatcherSignal();
        var first = signal.WaitAsync(CancellationToken.None);
        signal.Notify();
        await first.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(signal.WaitAsync(CancellationToken.None).IsCompleted);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Condition was not met in time.");
            await Task.Delay(5);
        }
    }
}
