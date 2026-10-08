using System.Net;
using System.Net.Http.Json;
using MessageBroker.Contracts;
using MessageBroker.Contracts.Models;
using MessageBroker.Domain;
using MessageBroker.IntegrationTests.Infrastructure;

namespace MessageBroker.IntegrationTests.Api;

public sealed class DeadLetterApiTests(SqlServerFixture sql) : ApiTest(sql)
{
    [Fact(DisplayName = "A08 DLQ pages newest first; the owner reads, others get 403; requeue is Admin only and resets attempts")]
    public async Task A08_DeadLetterQueue()
    {
        var (topic, publisher, _) = await ArrangeTopicAsync();
        var (ownerId, _, owner) = await CreateAppClientAsync();
        var (_, _, stranger) = await CreateAppClientAsync();
        var subscription = await CreateSubscriptionAsync(topic.TopicId, ownerId, maxAttempts: 1);

        // Three messages, each failing its only attempt.
        var deliveryIds = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            await PublishAsync(topic.Name, publisher);
            var leased = Assert.Single(await Deliveries.LeaseAsync(subscription.SubscriptionId, 1, "Pull", ownerId));
            await Deliveries.NackAsync(leased.DeliveryId, leased.LockToken, ownerId, new("Http500", $"failure {i}"));
            deliveryIds.Add(leased.DeliveryId);
        }

        var url = $"/api/v1/subscriptions/{subscription.SubscriptionId}/deadletters";
        var page1 = await ReadAsync<DeadLetterPage>(await owner.GetAsync($"{url}?pageSize=2"));
        Assert.Equal([deliveryIds[2], deliveryIds[1]], page1.Items.Select(d => d.DeliveryId));
        Assert.NotNull(page1.NextBefore);
        var newest = page1.Items[0];
        Assert.Equal(("MaxAttemptsExceeded", 1, "failure 2", topic.Name), (newest.Reason, newest.AttemptCount, newest.LastError, newest.TopicName));
        Assert.Equal("PAY-1", newest.Payload.GetProperty("paymentId").GetString());

        var page2 = await ReadAsync<DeadLetterPage>(await owner.GetAsync($"{url}?pageSize=2&before={page1.NextBefore}"));
        Assert.Equal([deliveryIds[0]], page2.Items.Select(d => d.DeliveryId));
        Assert.Null(page2.NextBefore);

        // Admins read any subscription's DLQ; other applications cannot.
        Assert.Equal(3, (await ReadAsync<DeadLetterPage>(await Admin.GetAsync(url))).Items.Count);
        await ProblemAsync(await stranger.GetAsync(url), HttpStatusCode.Forbidden, ProblemTypes.Forbidden);
        await ProblemAsync(await Admin.GetAsync($"/api/v1/subscriptions/{Guid.NewGuid()}/deadletters"), HttpStatusCode.NotFound, ProblemTypes.NotFound);

        // Requeue: Admin only.
        await ProblemAsync(await owner.PostAsync($"/api/v1/deadletters/{deliveryIds[0]}/requeue", null), HttpStatusCode.Forbidden, ProblemTypes.Forbidden);
        await ExpectAsync(Admin.PostAsync($"/api/v1/deadletters/{deliveryIds[0]}/requeue", null), HttpStatusCode.NoContent);

        var requeued = await GetDeliveryAsync(deliveryIds[0]);
        Assert.Equal((DeliveryStatus.Pending, 0, 1), (requeued.State, requeued.AttemptCount, requeued.TotalAttemptCount));
        Assert.NotNull(Assert.Single(await GetDeadLettersAsync(deliveryIds[0])).RequeuedBy);

        // Requeued entries leave the default listing but stay in the history.
        Assert.Equal(2, (await ReadAsync<DeadLetterPage>(await owner.GetAsync(url))).Items.Count);
        var all = await ReadAsync<DeadLetterPage>(await owner.GetAsync($"{url}?includeRequeued=true"));
        Assert.NotNull(all.Items.Single(d => d.DeliveryId == deliveryIds[0]).RequeuedAt);

        // The requeued delivery is received again with a fresh budget (attempt 1 of maxAttempts),
        // while the attempt history keeps numbering on (attempt row 2).
        var again = Assert.Single(await ReadAsync<List<Delivery>>(await owner.PostAsJsonAsync(
            $"/api/v1/subscriptions/{subscription.SubscriptionId}/receive", new ReceiveRequest())));
        Assert.Equal((deliveryIds[0], 1), (again.DeliveryId, again.Attempt));
        Assert.Equal([1, 2], (await GetAttemptsAsync(deliveryIds[0])).Select(a => a.AttemptNumber));

        // Requeue of something not in the DLQ is 409; unknown is 404.
        await ProblemAsync(await Admin.PostAsync($"/api/v1/deadletters/{deliveryIds[0]}/requeue", null), HttpStatusCode.Conflict, ProblemTypes.Conflict);
        await ProblemAsync(await Admin.PostAsync("/api/v1/deadletters/999999999/requeue", null), HttpStatusCode.NotFound, ProblemTypes.NotFound);
    }
}
