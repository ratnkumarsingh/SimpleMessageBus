using MessageBroker.Application.Persistence;
using MessageBroker.Application.Security;
using MessageBroker.Contracts.Models;
using MessageBroker.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MessageBroker.Application.Services;

/// <summary>Topic and subscription management. Admin only (spec section 12).</summary>
public sealed class TopologyService(
    ITopicRepository topics,
    ISubscriptionRepository subscriptions,
    IAllowedHostRepository allowedHosts,
    ISecretProtector secrets,
    IOptions<BrokerOptions> options,
    ILogger<TopologyService> logger)
{
    // ---- topics ----

    public async Task<TopicResponse> CreateTopicAsync(Caller caller, CreateTopicRequest request, CancellationToken ct)
    {
        caller.RequireAdmin();
        NameRules.Validate(request.Name, "name");
        ValidateTtl(request.DefaultTtlSeconds, "defaultTtlSeconds");

        var topic = await topics.CreateAsync(Guid.CreateVersion7(), request.Name!, request.DefaultTtlSeconds, ct);
        logger.LogInformation("Topic {Topic} created by {AppId}", topic.Name, caller.AppId);
        return topic.ToResponse();
    }

    public async Task<IReadOnlyList<TopicResponse>> ListTopicsAsync(Caller caller, CancellationToken ct)
    {
        caller.RequireAdmin();
        return (await topics.ListAsync(ct)).Select(t => t.ToResponse()).ToList();
    }

    public async Task<TopicResponse> GetTopicAsync(Caller caller, Guid topicId, CancellationToken ct)
    {
        caller.RequireAdmin();
        return (await topics.GetAsync(topicId, ct) ?? throw BrokerException.NotFound("Topic")).ToResponse();
    }

    public async Task<TopicResponse> UpdateTopicAsync(Caller caller, Guid topicId, UpdateTopicRequest request, CancellationToken ct)
    {
        caller.RequireAdmin();
        ValidateTtl(request.DefaultTtlSeconds, "defaultTtlSeconds");
        var topic = await topics.UpdateAsync(topicId, request.DefaultTtlSeconds, ct);
        logger.LogInformation("Topic {Topic} changed by {AppId}", topic.Name, caller.AppId);
        return topic.ToResponse();
    }

    public async Task DeleteTopicAsync(Caller caller, Guid topicId, CancellationToken ct)
    {
        caller.RequireAdmin();
        await topics.DeleteAsync(topicId, ct);
        logger.LogInformation("Topic {TopicId} deleted by {AppId}", topicId, caller.AppId);
    }

    // ---- subscriptions ----

    public async Task<SubscriptionResponse> CreateSubscriptionAsync(
        Caller caller, Guid topicId, CreateSubscriptionRequest request, CancellationToken ct)
    {
        caller.RequireAdmin();
        NameRules.Validate(request.Name, "name");
        if (request.OwnerAppId is not { } ownerAppId || ownerAppId == Guid.Empty)
            throw new BrokerValidationException([new("ownerAppId", "ownerAppId is required.")]);
        if (!EnumNames.TryParse<DeliveryMode>(request.DeliveryMode, out var mode))
            throw new BrokerValidationException([new("deliveryMode", "deliveryMode must be Webhook, SignalR or Pull.")]);

        var defaults = options.Value.Defaults;
        var settings = new SubscriptionSettings(
            mode,
            request.WebhookUrl,
            request.WebhookTimeoutSeconds ?? options.Value.Webhooks.TimeoutSeconds,
            request.MaxConcurrentDeliveries ?? defaults.MaxConcurrentDeliveries,
            request.MaxAttempts ?? defaults.MaxAttempts,
            request.LockDurationSeconds ?? defaults.LockDurationSeconds,
            request.RetryBaseDelaySeconds ?? defaults.RetryBaseDelaySeconds,
            request.RetryMaxDelaySeconds ?? defaults.RetryMaxDelaySeconds,
            request.TtlSeconds);
        await ValidateSettingsAsync(settings, checkAllowlist: true, ct);

        string? plainSecret = mode == DeliveryMode.Webhook ? ApiKeys.NewWebhookSecret() : null;
        var subscription = await subscriptions.CreateAsync(new SubscriptionCreate(
            Guid.CreateVersion7(), topicId, request.Name!, ownerAppId, mode.ToString(), settings.WebhookUrl,
            plainSecret is null ? null : secrets.Protect(plainSecret),
            settings.WebhookTimeoutSeconds, settings.MaxConcurrentDeliveries, settings.MaxAttempts,
            settings.LockDurationSeconds, settings.RetryBaseDelaySeconds, settings.RetryMaxDelaySeconds,
            settings.TtlSeconds), ct);

        logger.LogInformation("Subscription {SubscriptionId} ({DeliveryMode}) created on {Topic} by {AppId}",
            subscription.SubscriptionId, subscription.DeliveryMode, subscription.TopicName, caller.AppId);
        return subscription.ToResponse(plainSecret);
    }

    public async Task<IReadOnlyList<SubscriptionResponse>> ListSubscriptionsAsync(Caller caller, Guid topicId, CancellationToken ct)
    {
        caller.RequireAdmin();
        return (await subscriptions.ListAsync(topicId, ct)).Select(s => s.ToResponse()).ToList();
    }

    public async Task<SubscriptionResponse> GetSubscriptionAsync(Caller caller, Guid subscriptionId, CancellationToken ct)
    {
        caller.RequireAdmin();
        return (await GetExistingAsync(subscriptionId, ct)).ToResponse();
    }

    /// <summary>Merges the PATCH fields onto the current settings, validates the result, and saves it.</summary>
    public async Task<SubscriptionResponse> UpdateSubscriptionAsync(
        Caller caller, Guid subscriptionId, UpdateSubscriptionRequest request, CancellationToken ct)
    {
        caller.RequireAdmin();
        var current = await GetExistingAsync(subscriptionId, ct);

        var status = current.Status;
        if (request.Status is not null)
        {
            if (!EnumNames.TryParse<SubscriptionStatus>(request.Status, out var parsed)
                || parsed is not (SubscriptionStatus.Active or SubscriptionStatus.Paused))
                throw new BrokerValidationException([new("status", "status must be Active or Paused. Use DELETE to remove a subscription.")]);
            status = parsed.ToString();
        }

        var mode = Enum.Parse<DeliveryMode>(current.DeliveryMode);
        var settings = new SubscriptionSettings(
            mode,
            request.WebhookUrl ?? current.WebhookUrl,
            request.WebhookTimeoutSeconds ?? current.WebhookTimeoutSeconds,
            request.MaxConcurrentDeliveries ?? current.MaxConcurrentDeliveries,
            request.MaxAttempts ?? current.MaxAttempts,
            request.LockDurationSeconds ?? current.LockDurationSeconds,
            request.RetryBaseDelaySeconds ?? current.RetryBaseDelaySeconds,
            request.RetryMaxDelaySeconds ?? current.RetryMaxDelaySeconds,
            request.ClearTtl ? null : request.TtlSeconds ?? current.TtlSeconds);
        // A host removed from the allowlist later does not block pausing or tuning; only a new URL is checked.
        var urlChanged = request.WebhookUrl is not null && request.WebhookUrl != current.WebhookUrl;
        await ValidateSettingsAsync(settings, checkAllowlist: urlChanged, ct);

        var updated = await subscriptions.UpdateAsync(new SubscriptionUpdate(
            subscriptionId, status, settings.WebhookUrl, settings.WebhookTimeoutSeconds,
            settings.MaxConcurrentDeliveries, settings.MaxAttempts, settings.LockDurationSeconds,
            settings.RetryBaseDelaySeconds, settings.RetryMaxDelaySeconds, settings.TtlSeconds), ct);

        if (updated.Status != current.Status)
            logger.LogInformation("Subscription {SubscriptionId} {Change} by {AppId}", subscriptionId,
                updated.Status == nameof(SubscriptionStatus.Paused) ? "paused" : "resumed", caller.AppId);
        else
            logger.LogInformation("Subscription {SubscriptionId} changed by {AppId}", subscriptionId, caller.AppId);
        return updated.ToResponse();
    }

    public async Task DeleteSubscriptionAsync(Caller caller, Guid subscriptionId, CancellationToken ct)
    {
        caller.RequireAdmin();
        await subscriptions.DeleteAsync(subscriptionId, ct);
        logger.LogInformation("Subscription {SubscriptionId} deleted by {AppId}", subscriptionId, caller.AppId);
    }

    /// <summary>[Fix 7] Issues a new signing secret; the old one stays valid for 24 hours.</summary>
    public async Task<SubscriptionResponse> RotateSecretAsync(Caller caller, Guid subscriptionId, CancellationToken ct)
    {
        caller.RequireAdmin();
        var plainSecret = ApiKeys.NewWebhookSecret();
        var subscription = await subscriptions.RotateSecretAsync(subscriptionId, secrets.Protect(plainSecret), ct);
        logger.LogInformation("Webhook secret rotated for subscription {SubscriptionId} by {AppId}", subscriptionId, caller.AppId);
        return subscription.ToResponse(plainSecret);
    }

    // ---- helpers ----

    private async Task<SubscriptionRecord> GetExistingAsync(Guid subscriptionId, CancellationToken ct) =>
        await subscriptions.GetAsync(subscriptionId, ct) ?? throw BrokerException.NotFound("Subscription");

    private async Task ValidateSettingsAsync(SubscriptionSettings settings, bool checkAllowlist, CancellationToken ct)
    {
        var errors = SubscriptionSettingsValidator.Validate(settings, options.Value.Webhooks.RequireHttps).ToList();

        // [Fix 9] SSRF control: webhook hosts must be on the Admin-maintained allowlist.
        if (errors.Count == 0 && checkAllowlist && settings.DeliveryMode == DeliveryMode.Webhook)
        {
            var host = new Uri(settings.WebhookUrl!).Host;
            var allowed = await allowedHosts.ListAsync(ct);
            if (!allowed.Any(h => string.Equals(h.Host, host, StringComparison.OrdinalIgnoreCase)))
                errors.Add(new("webhookUrl", $"The host '{host}' is not on the webhook allowlist."));
        }

        BrokerValidationException.ThrowIfAny(errors);
    }

    private static void ValidateTtl(int? ttl, string field)
    {
        if (ttl is <= 0)
            throw new BrokerValidationException([new(field, $"{field} must be greater than zero.")]);
    }
}
