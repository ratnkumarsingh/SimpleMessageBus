using MessageBroker.Application;
using MessageBroker.Application.Dispatch;
using MessageBroker.Contracts;
using MessageBroker.Contracts.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MessageBroker.UnitTests.Application;

/// <summary>The admin activity feed: batching, the overview throttle, the bounded queue and the no-admin shortcut.</summary>
public class ActivityFeedTests
{
    private sealed class RecordingSink : IAdminActivitySink
    {
        public List<IReadOnlyList<MessageActivity>> Batches { get; } = [];
        public int OverviewPings { get; private set; }

        public Task SendActivityAsync(IReadOnlyList<MessageActivity> batch, CancellationToken ct)
        {
            Batches.Add(batch);
            return Task.CompletedTask;
        }

        public Task SendOverviewChangedAsync(CancellationToken ct)
        {
            OverviewPings++;
            return Task.CompletedTask;
        }
    }

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingSink _sink = new();
    private readonly BrokerActivityFeed _feed;

    public ActivityFeedTests() => _feed = new BrokerActivityFeed(_sink, _time, NullLogger<BrokerActivityFeed>.Instance);

    [Fact]
    public async Task Nothing_is_queued_or_sent_while_no_admin_is_connected()
    {
        _feed.Published(Guid.NewGuid());
        _feed.Changed();
        _feed.AdminConnected();
        await _feed.FlushAsync(CancellationToken.None);

        Assert.Empty(_sink.Batches);
        Assert.Equal(0, _sink.OverviewPings);
    }

    [Fact]
    public async Task Activity_is_sent_as_one_batch_in_order_with_kind_and_time()
    {
        _feed.AdminConnected();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        _feed.Published(a);
        _time.Advance(TimeSpan.FromMilliseconds(10));
        _feed.DeliveryChanged(b, 42);

        await _feed.FlushAsync(CancellationToken.None);

        var batch = Assert.Single(_sink.Batches);
        Assert.Equal(
            [new MessageActivity(ActivityKinds.Published, a, null, _time.GetUtcNow().UtcDateTime.AddMilliseconds(-10)),
             new MessageActivity(ActivityKinds.DeliveryChanged, b, 42, _time.GetUtcNow().UtcDateTime)],
            batch);
        Assert.Equal(1, _sink.OverviewPings);

        await _feed.FlushAsync(CancellationToken.None); // nothing new
        Assert.Single(_sink.Batches);
    }

    [Fact]
    public async Task Overview_ping_is_sent_at_most_once_per_second_and_only_after_changes()
    {
        _feed.AdminConnected();
        for (var tick = 0; tick < 8; tick++) // 2 seconds of 250 ms ticks with constant activity
        {
            _feed.Changed();
            await _feed.FlushAsync(CancellationToken.None);
            _time.Advance(BrokerActivityFeed.BatchInterval);
        }
        Assert.Equal(2, _sink.OverviewPings);

        // Changes after the last ping are not lost: they go out once the interval has passed.
        _time.Advance(TimeSpan.FromSeconds(5));
        await _feed.FlushAsync(CancellationToken.None);
        Assert.Equal(3, _sink.OverviewPings);

        _time.Advance(TimeSpan.FromSeconds(5));
        await _feed.FlushAsync(CancellationToken.None); // nothing changed since
        Assert.Equal(3, _sink.OverviewPings);
    }

    [Fact]
    public async Task Batches_are_capped_and_the_queue_drops_the_oldest_when_full()
    {
        _feed.AdminConnected();
        var ids = Enumerable.Range(0, BrokerActivityFeed.Capacity + 5).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids)
            _feed.Published(id);

        var sent = new List<MessageActivity>();
        do
        {
            _sink.Batches.Clear();
            await _feed.FlushAsync(CancellationToken.None);
            Assert.All(_sink.Batches, b => Assert.True(b.Count <= BrokerActivityFeed.MaxBatchSize));
            sent.AddRange(_sink.Batches.SelectMany(b => b));
        }
        while (_sink.Batches.Count > 0);

        Assert.Equal(ids.Skip(5), sent.Select(a => a.MessageId));
    }

    [Fact]
    public async Task Queued_activity_is_dropped_when_the_last_admin_leaves()
    {
        _feed.AdminConnected();
        _feed.Published(Guid.NewGuid());
        _feed.AdminDisconnected();
        await _feed.FlushAsync(CancellationToken.None);
        _feed.AdminConnected();
        await _feed.FlushAsync(CancellationToken.None);

        Assert.Empty(_sink.Batches);
    }

    [Fact]
    public void The_no_op_feed_and_push_status_are_the_defaults()
    {
        using var services = new ServiceCollection().AddLogging().AddBrokerApplication().BuildServiceProvider();
        Assert.IsType<NullBrokerActivityFeed>(services.GetRequiredService<IBrokerActivityFeed>());
        Assert.IsType<NullPushStatus>(services.GetRequiredService<IPushStatus>());
    }

    [Fact]
    public void AddBrokerActivityFeed_replaces_the_default_with_one_shared_feed()
    {
        using var services = new ServiceCollection().AddLogging().AddBrokerApplication()
            .AddBrokerActivityFeed<RecordingSink>().BuildServiceProvider();
        var feed = services.GetRequiredService<IBrokerActivityFeed>();
        Assert.IsType<BrokerActivityFeed>(feed);
        Assert.Same(services.GetRequiredService<BrokerActivityFeed>(), feed);
    }
}
