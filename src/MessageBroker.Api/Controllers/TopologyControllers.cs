using MessageBroker.Api.Auth;
using MessageBroker.Application.Security;
using MessageBroker.Application.Services;
using MessageBroker.Contracts.Models;
using Microsoft.AspNetCore.Mvc;

namespace MessageBroker.Api.Controllers;

[ApiController]
public abstract class BrokerController : ControllerBase
{
    protected Caller Caller => User.ToCaller();
}

/// <summary>Topics (Admin).</summary>
[Route("api/v1/topics")]
public sealed class TopicsController(TopologyService topology) : BrokerController
{
    [HttpPost]
    public async Task<ActionResult<TopicResponse>> Create(CreateTopicRequest request, CancellationToken ct)
    {
        var topic = await topology.CreateTopicAsync(Caller, request, ct);
        return CreatedAtAction(nameof(Get), new { topicId = topic.TopicId }, topic);
    }

    [HttpGet]
    public Task<IReadOnlyList<TopicResponse>> List(CancellationToken ct) => topology.ListTopicsAsync(Caller, ct);

    [HttpGet("{topicId:guid}")]
    public Task<TopicResponse> Get(Guid topicId, CancellationToken ct) => topology.GetTopicAsync(Caller, topicId, ct);

    [HttpPatch("{topicId:guid}")]
    public Task<TopicResponse> Update(Guid topicId, UpdateTopicRequest request, CancellationToken ct) =>
        topology.UpdateTopicAsync(Caller, topicId, request, ct);

    /// <summary>409 while the topic still has subscriptions.</summary>
    [HttpDelete("{topicId:guid}")]
    public async Task<IActionResult> Delete(Guid topicId, CancellationToken ct)
    {
        await topology.DeleteTopicAsync(Caller, topicId, ct);
        return NoContent();
    }

    [HttpPost("{topicId:guid}/subscriptions")]
    public async Task<ActionResult<SubscriptionResponse>> CreateSubscription(
        Guid topicId, CreateSubscriptionRequest request, CancellationToken ct)
    {
        var subscription = await topology.CreateSubscriptionAsync(Caller, topicId, request, ct);
        return CreatedAtAction(nameof(SubscriptionsController.Get), "Subscriptions",
            new { subscriptionId = subscription.SubscriptionId }, subscription);
    }

    [HttpGet("{topicId:guid}/subscriptions")]
    public Task<IReadOnlyList<SubscriptionResponse>> ListSubscriptions(Guid topicId, CancellationToken ct) =>
        topology.ListSubscriptionsAsync(Caller, topicId, ct);
}

/// <summary>Subscriptions (Admin). Receive, ACK and NACK live on the delivery endpoints.</summary>
[Route("api/v1/subscriptions")]
public sealed class SubscriptionsController(TopologyService topology) : BrokerController
{
    [HttpGet("{subscriptionId:guid}")]
    public Task<SubscriptionResponse> Get(Guid subscriptionId, CancellationToken ct) =>
        topology.GetSubscriptionAsync(Caller, subscriptionId, ct);

    /// <summary>Change settings, or pause and resume with status Paused / Active.</summary>
    [HttpPatch("{subscriptionId:guid}")]
    public Task<SubscriptionResponse> Update(Guid subscriptionId, UpdateSubscriptionRequest request, CancellationToken ct) =>
        topology.UpdateSubscriptionAsync(Caller, subscriptionId, request, ct);

    /// <summary>Soft delete. Pending and leased deliveries are cancelled.</summary>
    [HttpDelete("{subscriptionId:guid}")]
    public async Task<IActionResult> Delete(Guid subscriptionId, CancellationToken ct)
    {
        await topology.DeleteSubscriptionAsync(Caller, subscriptionId, ct);
        return NoContent();
    }

    /// <summary>Issues a new webhook signing secret; the previous one stays valid for 24 hours.</summary>
    [HttpPost("{subscriptionId:guid}/webhook-secret")]
    public Task<SubscriptionResponse> RotateSecret(Guid subscriptionId, CancellationToken ct) =>
        topology.RotateSecretAsync(Caller, subscriptionId, ct);
}
