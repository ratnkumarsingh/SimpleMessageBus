using MessageBroker.Contracts;
using MessageBroker.Contracts.Models;
using MessageBroker.Dashboard.Components.Pages;
using MessageBroker.Dashboard.Components.Shared;
using MessageBroker.Dashboard.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MessageBroker.Dashboard.Tests;

/// <summary>V02–V08: the dashboard pages against a fake broker and a hand-driven live feed.</summary>
public sealed class PageTests : PageTest
{
    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TopicId = Guid.NewGuid();

    private static MessageListItem Item(Guid id, string status = "InProgress") => new()
    {
        MessageId = id, TopicName = "payments", MessageType = "PaymentProcessed.v1", CorrelationId = "PAY-1",
        PublisherName = "billing", CreatedAt = Now, Status = status, DeliveryCount = 2, Completed = 1, Pending = 1,
    };

    [Fact(DisplayName = "V02 Overview renders tiles, chart legend and subscription health, and re-reads on OverviewChanged")]
    public void V02_Overview()
    {
        Broker.Overview = new OverviewResponse
        {
            Totals = new OverviewTotals { Pending = 12, Leased = 3, DeadLettered = 2, PublishedInWindow = 15_400, CompletedInWindow = 15_000, WindowMinutes = 60 },
            Series = Enumerable.Range(0, 60).Select(i => new ThroughputPoint(Now.AddMinutes(i - 59), i, i, 0, i == 59 ? 1 : 0)).ToList(),
            Subscriptions =
            [
                new SubscriptionHealth { SubscriptionId = Guid.NewGuid(), TopicId = TopicId, TopicName = "payments", Name = "ledger", Status = "Active", DeliveryMode = "Webhook", Pending = 12, DeadLettered = 2, CircuitState = "Open" },
                new SubscriptionHealth { SubscriptionId = Guid.NewGuid(), TopicId = TopicId, TopicName = "payments", Name = "ui", Status = "Paused", DeliveryMode = "SignalR", ConnectedClients = 0 },
            ],
            DispatcherHeartbeatAgeSeconds = 3,
        };

        var cut = Render<Overview>();

        var tiles = cut.FindAll(".tile").Select(t => t.TextContent).ToList();
        Assert.Contains(tiles, t => t.Contains("Pending deliveries") && t.Contains("12"));
        Assert.Contains(tiles, t => t.Contains("Published") && t.Contains("15.4K"));
        Assert.Single(cut.FindAll(".tile.emphasis")); // the DLQ tile
        Assert.Equal(["Published", "Completed", "Failed attempts", "Dead-lettered"],
            cut.FindAll(".legend li").Select(li => li.ChildNodes.OfType<AngleSharp.Dom.IText>().First().Text.Trim()));
        Assert.Contains("Circuit open", cut.Markup);
        Assert.Contains("0 connected", cut.Markup);
        Assert.Contains("Dispatcher healthy", cut.Markup);
        Assert.Contains($"deadletters?subscriptionId={Broker.Overview.Subscriptions[0].SubscriptionId}", cut.Markup);

        // The chart's table view carries every value without hovering.
        cut.Find("figcaption button").Click();
        Assert.Equal(60, cut.FindAll("figure table tbody tr").Count);

        var before = Broker.CallCount(nameof(IBrokerAdminClient.GetOverviewAsync));
        Live.RaiseOverviewChanged();
        cut.WaitForAssertion(() => Assert.True(Broker.CallCount(nameof(IBrokerAdminClient.GetOverviewAsync)) > before));
    }

    [Fact(DisplayName = "V03 Message filters come from the query string, map to the search request, and Apply writes them back")]
    public void V03_MessageFilters()
    {
        Broker.Topics.Add(new TopicResponse(TopicId, "payments", null, Now));
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"messages?status=DeadLettered&topicId={TopicId}&correlationId=PAY-9&from=2026-10-08T10:00");

        var cut = Render<Messages>();

        var search = Assert.Single(Broker.Searches);
        Assert.Equal(("DeadLettered", TopicId, "PAY-9", Messages.PageSize), (search.Status, search.TopicId, search.CorrelationId, search.PageSize));
        Assert.Equal(new DateTime(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc), search.From);
        Assert.Equal(DateTimeKind.Utc, search.From!.Value.Kind);
        Assert.Equal("DeadLettered", cut.Find("#f-status").GetAttribute("value"));

        cut.Find("#f-status").Change("InProgress");
        cut.Find("#f-type").Change("Refund.v1");
        cut.Find("form").Submit();

        Assert.Contains("status=InProgress", nav.Uri);
        Assert.Contains("messageType=Refund.v1", nav.Uri);
        Assert.Contains($"topicId={TopicId}", nav.Uri);
        cut.WaitForAssertion(() => Assert.Equal("Refund.v1", Broker.Searches.Last().MessageType));
    }

    [Fact(DisplayName = "V03b Load more passes the cursor and appends rows")]
    public void V03b_LoadMore()
    {
        var first = Enumerable.Range(0, 2).Select(_ => Item(Guid.NewGuid())).ToList();
        var second = Item(Guid.NewGuid());
        Broker.Search = q => q.Cursor is null ? new(first, 42) : new([second], null);

        var cut = Render<Messages>();
        cut.Find(".more button").Click();

        Assert.Equal(42, Broker.Searches.Last().Cursor);
        Assert.Equal(3, cut.FindAll("tbody tr").Count);
        Assert.Empty(cut.FindAll(".more button"));
    }

    [Fact(DisplayName = "V04 Published activity shows a 'new messages' banner; DeliveryChanged refreshes a visible row in place")]
    public void V04_LiveMessages()
    {
        var visible = Guid.NewGuid();
        Broker.Search = _ => new([Item(visible)], null);
        Broker.MessagesById[visible] = new MessageResponse
        {
            MessageId = visible, Status = "Completed", Payload = Json("{}"),
            Deliveries = [new MessageDeliveryResponse { Status = "Completed" }, new MessageDeliveryResponse { Status = "Completed" }],
        };
        var cut = Render<Messages>();
        Assert.Empty(cut.FindAll(".notice.info"));

        Live.RaiseActivity(
            new MessageActivity(ActivityKinds.Published, Guid.NewGuid(), null, Now),
            new MessageActivity(ActivityKinds.Published, Guid.NewGuid(), null, Now),
            new MessageActivity(ActivityKinds.DeliveryChanged, visible, 7, Now));

        cut.WaitForAssertion(() => Assert.Contains("2 new messages published", cut.Find(".notice.info").TextContent));
        cut.WaitForAssertion(() => Assert.Contains("Completed", cut.Find("tbody tr").TextContent));
        Assert.Contains("2 / 2", cut.Find("tbody tr").TextContent);
        Assert.Single(Broker.Searches); // rows were patched, not re-queried

        cut.Find(".notice.info button").Click();
        Assert.Empty(cut.FindAll(".notice.info"));
        Assert.Equal(2, Broker.Searches.Count);
    }

    [Fact(DisplayName = "V05 Message detail renders deliveries and attempts, and reloads on its own activity only")]
    public void V05_MessageDetail()
    {
        var id = Guid.NewGuid();
        Broker.MessagesById[id] = new MessageResponse
        {
            MessageId = id, TopicName = "payments", MessageType = "PaymentProcessed.v1", CorrelationId = "PAY-5",
            Status = "PartiallyDeadLettered", CreatedAt = Now, Payload = Json("""{"paymentId":"PAY-5","amount":10}"""),
            Properties = Json("""{"tenant":"in-01"}"""),
            Deliveries =
            [
                new MessageDeliveryResponse
                {
                    DeliveryId = 1, SubscriptionName = "ledger", DeliveryMode = "Webhook", Status = "DeadLettered", AttemptCount = 2,
                    TotalAttemptCount = 2, DeadLetterReason = "MaxAttemptsExceeded",
                    Attempts =
                    [
                        new DeliveryAttemptResponse { AttemptNumber = 1, Channel = "Webhook", LeasedAt = Now, EndedAt = Now.AddSeconds(1), Outcome = "Nacked", HttpStatusCode = 500, DurationMs = 812, ErrorCode = "Http500", ErrorMessage = "Server error" },
                        new DeliveryAttemptResponse { AttemptNumber = 2, Channel = "Webhook", LeasedAt = Now.AddSeconds(30), EndedAt = Now.AddSeconds(60), Outcome = "LeaseExpired", ErrorCode = "LeaseExpired" },
                    ],
                },
                new MessageDeliveryResponse { DeliveryId = 2, SubscriptionName = "ui", DeliveryMode = "SignalR", Status = "Completed", Attempts = [new DeliveryAttemptResponse { AttemptNumber = 1, Channel = "SignalR", LeasedAt = Now, EndedAt = Now, Outcome = "Acked", DurationMs = 40 }] },
            ],
        };

        var cut = Render<MessageDetail>(p => p.Add(x => x.MessageId, id));

        Assert.Contains("\"paymentId\": \"PAY-5\"", cut.Markup);
        Assert.Contains("\"tenant\": \"in-01\"", cut.Markup);
        Assert.Equal(2, cut.FindAll(".delivery").Count);
        Assert.Equal(3, cut.FindAll(".timeline li").Count);
        Assert.Contains("HTTP 500", cut.Markup);
        Assert.Contains("812 ms", cut.Markup);
        Assert.Contains("Lease expired", cut.Markup);
        Assert.Contains("open in dead letters", cut.Markup);
        Assert.Contains("messages?correlationId=PAY-5", cut.Markup);

        var loads = Broker.CallCount(nameof(IBrokerAdminClient.GetMessageAsync));
        Live.RaiseActivity(new MessageActivity(ActivityKinds.DeliveryChanged, Guid.NewGuid(), 99, Now));
        Live.RaiseOverviewChanged(); // not InProgress: no polling
        Assert.Equal(loads, Broker.CallCount(nameof(IBrokerAdminClient.GetMessageAsync)));

        Live.RaiseActivity(new MessageActivity(ActivityKinds.DeliveryChanged, id, 1, Now));
        cut.WaitForAssertion(() => Assert.Equal(loads + 1, Broker.CallCount(nameof(IBrokerAdminClient.GetMessageAsync))));
    }

    [Fact(DisplayName = "V05b An unknown message shows 'not found' instead of an error")]
    public void V05b_MessageNotFound()
    {
        var cut = Render<MessageDetail>(p => p.Add(x => x.MessageId, Guid.NewGuid()));
        Assert.Contains("Message not found", cut.Markup);
        Assert.Empty(cut.FindAll(".delivery"));
    }

    [Fact(DisplayName = "V06 Bulk requeue asks first, calls the broker per selected row, and reports each failure")]
    public void V06_BulkRequeue()
    {
        for (var i = 1; i <= 3; i++)
            Broker.DeadLetters.Add(new DeadLetterResponse
            {
                DeadLetterId = 10 + i, DeliveryId = i, MessageId = Guid.NewGuid(), TopicName = "payments", SubscriptionName = "ledger",
                Reason = "MaxAttemptsExceeded", AttemptCount = 4, LastError = $"failure {i}", DeadLetteredAt = Now, Payload = Json("null"),
            });
        Broker.FailingRequeues.Add(2);

        var cut = Render<DeadLetters>();
        Assert.Equal(3, cut.FindAll("tbody tr").Count);
        Assert.Contains("Attempts exhausted", cut.Markup);

        cut.Find("input[aria-label='Select delivery 1']").Change(true);
        cut.Find("input[aria-label='Select delivery 2']").Change(true);
        Assert.Contains("2 selected", cut.Markup);

        cut.FindAll("button").Single(b => b.TextContent == "Requeue selected").Click();
        Assert.Empty(Broker.Requeued); // nothing happens before confirming
        Assert.Contains("Requeue 2 deliveries?", cut.Find("[role=alertdialog]").TextContent);

        cut.Find("[role=alertdialog] button.primary").Click();

        cut.WaitForAssertion(() => Assert.Contains("Requeued 1 of 2; 1 failed.", cut.Markup));
        Assert.Equal([1L, 2L], Broker.Requeued.Order());
        Assert.Contains("Delivery 2: 409 Conflict", cut.Markup);
        Assert.Equal(2, cut.FindAll("tbody tr").Count); // the requeued one left the default list
    }

    [Fact(DisplayName = "V06b Cancelling the confirmation requeues nothing")]
    public void V06b_CancelRequeue()
    {
        Broker.DeadLetters.Add(new DeadLetterResponse { DeadLetterId = 1, DeliveryId = 1, MessageId = Guid.NewGuid(), Reason = "Expired", DeadLetteredAt = Now, Payload = Json("null") });
        var cut = Render<DeadLetters>();

        cut.FindAll("tbody button").Single(b => b.TextContent == "Requeue").Click();
        cut.Find("[role=alertdialog] button.secondary").Click();

        Assert.Empty(cut.FindAll("[role=alertdialog]"));
        Assert.Empty(Broker.Requeued);
    }

    [Fact(DisplayName = "V07 Topology shows the webhook host but never its path, query string or secret")]
    public void V07_TopologyHidesSecrets()
    {
        var owner = Guid.NewGuid();
        Broker.Topics.Add(new TopicResponse(TopicId, "payments", 3600, Now));
        Broker.Applications.Add(new ApplicationResponse(owner, "ledger-service", false, true, Now));
        Broker.SubscriptionsByTopic[TopicId] =
        [
            new SubscriptionResponse
            {
                SubscriptionId = Guid.NewGuid(), TopicId = TopicId, TopicName = "payments", Name = "ledger", OwnerAppId = owner,
                Status = "Active", DeliveryMode = "Webhook", WebhookUrl = "https://hooks.example.com/secret-path?token=abc123",
                WebhookSecret = "whsec_do_not_show", MaxAttempts = 4, LockDurationSeconds = 60, RetryBaseDelaySeconds = 30,
                RetryMaxDelaySeconds = 900, Counts = new DeliveryCountsResponse(5, 1, 0),
            },
        ];

        var cut = Render<Topology>();

        Assert.Contains("hooks.example.com", cut.Markup);
        Assert.Contains("ledger-service", cut.Markup);
        Assert.DoesNotContain("secret-path", cut.Markup);
        Assert.DoesNotContain("abc123", cut.Markup);
        Assert.DoesNotContain("whsec_do_not_show", cut.Markup);

        cut.FindAll("[role=tab]")[1].Click();
        Assert.Contains("ledger-service", cut.Find("tbody").TextContent);
    }

    [Theory(DisplayName = "V08 The live badge names each feed state")]
    [InlineData(LiveState.Live, "Live")]
    [InlineData(LiveState.Reconnecting, "Reconnecting")]
    [InlineData(LiveState.Offline, "Offline · polling")]
    [InlineData(LiveState.Connecting, "Connecting")]
    public void V08_LiveBadge(LiveState state, string text)
    {
        var cut = Render<LiveBadge>(p => p.Add(x => x.State, state));
        Assert.Equal(text, cut.Find(".live-badge").TextContent.Trim());
        Assert.Contains(state.ToString().ToLowerInvariant(), cut.Find(".live-badge").ClassList);
    }
}
