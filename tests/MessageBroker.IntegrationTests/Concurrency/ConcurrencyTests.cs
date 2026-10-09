using System.Collections.Concurrent;
using System.Diagnostics;
using MessageBroker.Application;
using MessageBroker.Application.Dispatch;
using MessageBroker.Application.Persistence;
using MessageBroker.Domain;
using MessageBroker.IntegrationTests.Infrastructure;
using MessageBroker.Worker.Maintenance;
using MessageBroker.Worker.Push;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace MessageBroker.IntegrationTests.Concurrency;

/// <summary>
/// C01, C02 — many competing consumers, dispatcher instances and maintenance passes on one database.
/// Each consumer occasionally "crashes" (drops a lease without settling it) so the lease-expiry path
/// races the normal path throughout.
/// </summary>
public sealed class ConcurrencyTests(SqlServerFixture sql, ITestOutputHelper output) : DatabaseTest(sql)
{
    private const int LockSeconds = 2;

    /// <summary>Who has settled what. A delivery's attempt is handed out once; an ACK succeeds once.</summary>
    private sealed class Ledger
    {
        public ConcurrentDictionary<(long DeliveryId, int Attempt), string> Handed { get; } = new();
        public ConcurrentDictionary<long, int> Acks { get; } = new();
        public int DuplicateHandOuts;
        public int Abandoned;
        public int LostLeases;
        public int Transient;

        public void Record(LeasedDeliveryRecord delivery, string consumer)
        {
            if (!Handed.TryAdd((delivery.DeliveryId, delivery.Attempt), consumer))
                Interlocked.Increment(ref DuplicateHandOuts);
        }

        /// <summary>Settles like a well-behaved consumer: abandons ~2%, retries transient store errors.</summary>
        public async Task SettleAsync(IDeliveryRepository deliveries, LeasedDeliveryRecord delivery, Guid? appId)
        {
            if (Random.Shared.Next(100) < 2)
            {
                Interlocked.Increment(ref Abandoned);
                return;
            }
            await Task.Delay(Random.Shared.Next(3));
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await deliveries.AckAsync(delivery.DeliveryId, delivery.LockToken, appId);
                    Acks.AddOrUpdate(delivery.DeliveryId, 1, (_, n) => n + 1);
                    return;
                }
                catch (BrokerException ex) when (ex.Kind == BrokerErrorKind.LeaseLost)
                {
                    Interlocked.Increment(ref LostLeases);
                    return;
                }
                catch (BrokerException ex) when (ex.Kind == BrokerErrorKind.Unavailable && attempt < 5)
                {
                    Interlocked.Increment(ref Transient); // deadlock victim: the transaction rolled back
                }
            }
        }
    }

    /// <summary>Stands in for the webhook channel: settles straight through the repository.</summary>
    private sealed class SettlingChannel(IDeliveryRepository deliveries, Ledger ledger, string name) : IPushChannel
    {
        public string Mode => "Webhook";

        public int Allowance(PushSubscriptionRecord subscription) => int.MaxValue;

        public async Task DeliverAsync(PushSubscriptionRecord subscription, LeasedDeliveryRecord delivery, CancellationToken ct)
        {
            ledger.Record(delivery, name);
            await ledger.SettleAsync(deliveries, delivery, appId: null);
        }
    }

    [Fact(DisplayName = "C01 20 pull receivers + 2 dispatchers + 2 maintenance loops over 10,000 deliveries: each completed exactly once, no duplicate (DeliveryId, AttemptNumber)")]
    public async Task C01_CompetingConsumers()
    {
        const int Messages = 5_000, Receivers = 20;
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var pull = await CreateSubscriptionAsync(topic.TopicId, subscriber, maxAttempts: 100, lockSeconds: LockSeconds, retryBase: 1, retryMax: 1);
        await CreateSubscriptionAsync(topic.TopicId, subscriber, mode: "Webhook", maxAttempts: 100, lockSeconds: LockSeconds,
            retryBase: 1, retryMax: 1, maxConcurrent: 50, webhookTimeout: 1);

        var clock = Stopwatch.StartNew();
        await Parallel.ForAsync(0, Messages, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (_, _) => await PublishAsync(topic.Name, publisher));
        output.WriteLine($"Published {Messages} messages (x2 subscriptions) in {clock.Elapsed.TotalSeconds:F1} s");
        Assert.Equal(2 * Messages, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Deliveries"));

        var ledger = new Ledger();
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var options = Options.Create(new BrokerOptions { Dispatcher = { PollIntervalMs = 50 } });
        var dispatchers = Enumerable.Range(1, 2).Select(i => new LeaseLoop(Subscriptions, Deliveries,
            [new SettlingChannel(Deliveries, ledger, $"dispatcher-{i}")], new DispatcherSignal(), new NullBrokerActivityFeed(),
            options, TimeProvider.System, NullLogger<LeaseLoop>.Instance)).ToList();
        var maintenance = new MaintenanceLoop(Deliveries, Operations, new NullBrokerActivityFeed(), options, TimeProvider.System, NullLogger<MaintenanceLoop>.Instance);

        clock.Restart();
        foreach (var dispatcher in dispatchers)
            await dispatcher.StartAsync(CancellationToken.None);
        var workers = new List<Task>();
        for (var i = 0; i < Receivers; i++)
        {
            var name = $"receiver-{i}";
            workers.Add(Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    IReadOnlyList<LeasedDeliveryRecord> batch;
                    try
                    {
                        batch = await Deliveries.LeaseAsync(pull.SubscriptionId, 10, "Pull", subscriber);
                    }
                    catch (BrokerException ex) when (ex.Kind == BrokerErrorKind.Unavailable)
                    {
                        Interlocked.Increment(ref ledger.Transient);
                        continue;
                    }
                    if (batch.Count == 0)
                        await Task.Delay(50);
                    foreach (var delivery in batch)
                    {
                        ledger.Record(delivery, name);
                        await ledger.SettleAsync(Deliveries, delivery, subscriber);
                    }
                }
            }));
        }
        for (var i = 0; i < 2; i++)
        {
            workers.Add(Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        await maintenance.RunOnceAsync(CancellationToken.None);
                    }
                    catch (BrokerException ex) when (ex.Kind == BrokerErrorKind.Unavailable)
                    {
                        Interlocked.Increment(ref ledger.Transient);
                    }
                    await Task.Delay(200);
                }
            }));
        }

        while (!stop.IsCancellationRequested && await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Deliveries WHERE Status <> 2") > 0)
            await Task.Delay(250);
        var drained = clock.Elapsed;
        await stop.CancelAsync();
        await Task.WhenAll(workers);
        foreach (var dispatcher in dispatchers)
            await dispatcher.StopAsync(CancellationToken.None);
        output.WriteLine($"Drained in {drained.TotalSeconds:F1} s; hand-outs {ledger.Handed.Count}, abandoned {ledger.Abandoned}, " +
            $"lost leases {ledger.LostLeases}, transient store errors {ledger.Transient}");

        // Every delivery completed, and its ACK succeeded exactly once.
        Assert.Equal(2 * Messages, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.Deliveries WHERE Status = 2"));
        Assert.Equal(2 * Messages, ledger.Acks.Count);
        Assert.All(ledger.Acks, a => Assert.Equal(1, a.Value));

        // No attempt was handed to two consumers, and the attempt history agrees.
        Assert.Equal(0, ledger.DuplicateHandOuts);
        Assert.Contains(ledger.Handed.Values, v => v.StartsWith("dispatcher-1"));
        Assert.Contains(ledger.Handed.Values, v => v.StartsWith("dispatcher-2"));
        Assert.Equal(0, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM (SELECT DeliveryId FROM broker.DeliveryAttempts GROUP BY DeliveryId, AttemptNumber HAVING COUNT(*) > 1) d"));
        Assert.Equal(ledger.Handed.Count, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.DeliveryAttempts"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.DeliveryAttempts WHERE EndedAt IS NULL"));
        Assert.Equal(2 * Messages, await ScalarAsync<int>("SELECT COUNT(*) FROM broker.DeliveryAttempts WHERE Outcome = 'Acked'"));
        Assert.Equal(0, await ScalarAsync<int>("""
            SELECT COUNT(*) FROM broker.Deliveries d
            WHERE d.TotalAttemptCount <> (SELECT COUNT(*) FROM broker.DeliveryAttempts a WHERE a.DeliveryId = d.DeliveryId)
               OR 1 <> (SELECT COUNT(*) FROM broker.DeliveryAttempts a WHERE a.DeliveryId = d.DeliveryId AND a.Outcome = 'Acked')
            """));

        // The crash path ran: every abandoned lease expired and was retried.
        Assert.True(ledger.Abandoned > 0);
        Assert.Equal(ledger.Abandoned + ledger.LostLeases,
            await ScalarAsync<int>("SELECT COUNT(*) FROM broker.DeliveryAttempts WHERE Outcome = 'LeaseExpired'"));
        Assert.Empty(await QueryAsync<long>("SELECT DeliveryId FROM broker.DeadLetters", r => Col<long>(r, 0)));
    }

    [Fact(DisplayName = "C02 ACK/NACK racing lease expiry: exactly one wins and the delivery's state agrees with the winner")]
    public async Task C02_SettleRacesExpiry()
    {
        const int Count = 200;
        var (topic, publisher, subscriber) = await ArrangeTopicAsync();
        var sub = await CreateSubscriptionAsync(topic.TopicId, subscriber, maxAttempts: 10, lockSeconds: 1, retryBase: 30, retryMax: 30);
        for (var i = 0; i < Count; i++)
            await PublishAsync(topic.Name, publisher);
        var leased = await Deliveries.LeaseAsync(sub.SubscriptionId, Count, "Pull", subscriber);
        Assert.Equal(Count, leased.Count);

        // Settles land spread across the 1 s lease while maintenance expires leases as fast as it can,
        // so the ones near the deadline race it.
        using var racing = new CancellationTokenSource();
        var expirer = Task.Run(async () =>
        {
            while (!racing.IsCancellationRequested)
            {
                try
                {
                    await Deliveries.ExpireLeasesAsync();
                }
                catch (BrokerException ex) when (ex.Kind == BrokerErrorKind.Unavailable)
                {
                }
            }
        });
        var outcomes = await Task.WhenAll(leased.Select(async (delivery, i) =>
        {
            await Task.Delay(Random.Shared.Next(1_600));
            var nack = i % 2 == 1;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (nack)
                        await Deliveries.NackAsync(delivery.DeliveryId, delivery.LockToken, subscriber, new FailureDetails("Busy", "try later"));
                    else
                        await Deliveries.AckAsync(delivery.DeliveryId, delivery.LockToken, subscriber);
                    return (delivery.DeliveryId, Nack: nack, Won: true);
                }
                catch (BrokerException ex) when (ex.Kind == BrokerErrorKind.LeaseLost)
                {
                    return (delivery.DeliveryId, Nack: nack, Won: false);
                }
                catch (BrokerException ex) when (ex.Kind == BrokerErrorKind.Unavailable && attempt < 5)
                {
                }
            }
        }));
        await Task.Delay(TimeSpan.FromSeconds(LockSeconds));
        await racing.CancelAsync();
        await expirer;
        await Deliveries.ExpireLeasesAsync();

        output.WriteLine($"ACK won {outcomes.Count(o => !o.Nack && o.Won)}, NACK won {outcomes.Count(o => o.Nack && o.Won)}, " +
            $"expiry won {outcomes.Count(o => !o.Won)}");
        Assert.Contains(outcomes, o => o.Won);
        Assert.Contains(outcomes, o => !o.Won);

        foreach (var (deliveryId, nack, won) in outcomes)
        {
            var row = await GetDeliveryAsync(deliveryId);
            var attempt = Assert.Single(await GetAttemptsAsync(deliveryId));
            Assert.NotNull(attempt.EndedAt);
            Assert.Null(row.LockToken);
            Assert.Equal(1, row.TotalAttemptCount);
            var expected = (won, nack) switch
            {
                (true, false) => (DeliveryStatus.Completed, "Acked"),
                (true, true) => (DeliveryStatus.Pending, "Nacked"),
                _ => (DeliveryStatus.Pending, "LeaseExpired"),
            };
            Assert.Equal(expected, (row.State, attempt.Outcome));
        }
    }
}
