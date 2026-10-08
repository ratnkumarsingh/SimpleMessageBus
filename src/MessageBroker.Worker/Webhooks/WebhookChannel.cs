using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MessageBroker.Application.Dispatch;
using MessageBroker.Application.Persistence;
using MessageBroker.Application.Security;
using MessageBroker.Application.Services;
using MessageBroker.Contracts.Webhooks;
using MessageBroker.Domain;
using MessageBroker.Worker.Push;
using Microsoft.Extensions.Logging;

namespace MessageBroker.Worker.Webhooks;

/// <summary>
/// Webhook delivery (spec sections 8.2 and 9): a signed POST of the delivery JSON. The response
/// settles the delivery: 2xx ACKs, 202 leaves the lease open for a later REST ACK, anything else
/// (including a timeout, a connection failure or a redirect) NACKs it into the retry path.
/// </summary>
public sealed class WebhookChannel(
    IHttpClientFactory httpClients,
    IDeliveryRepository deliveries,
    ISecretProtector secrets,
    CircuitBreakerRegistry circuits,
    IBrokerActivityFeed activity,
    TimeProvider time,
    ILogger<WebhookChannel> logger) : IPushChannel
{
    public const string HttpClientName = "broker-webhooks";
    private const int MaxErrorDetailChars = 2000;

    public string Mode => nameof(DeliveryMode.Webhook);

    public int Allowance(PushSubscriptionRecord subscription) => circuits.Allowance(subscription.SubscriptionId);

    public Task DeliverAsync(PushSubscriptionRecord subscription, LeasedDeliveryRecord delivery, CancellationToken ct)
    {
        circuits.OnAttemptStarted(subscription.SubscriptionId); // before any await: limits the half-open trial to one
        return SendAndSettleAsync(subscription, delivery, ct);
    }

    private async Task SendAndSettleAsync(PushSubscriptionRecord subscription, LeasedDeliveryRecord delivery, CancellationToken ct)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["MessageId"] = delivery.MessageId,
            ["CorrelationId"] = delivery.CorrelationId,
            ["DeliveryId"] = delivery.DeliveryId,
            ["SubscriptionId"] = subscription.SubscriptionId,
        });

        HttpRequestMessage request;
        try
        {
            request = BuildRequest(subscription, delivery);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not the endpoint's fault (e.g. Data Protection keys missing), so the circuit is untouched.
            logger.LogError(ex, "Webhook request for delivery {DeliveryId} could not be signed", delivery.DeliveryId);
            await SettleAsync(delivery, new WebhookResult(WebhookDecision.Nack, null, "SigningFailed", "The webhook secret could not be read."), null);
            return;
        }

        var started = time.GetTimestamp();
        WebhookResult result;
        string? errorDetail = null;
        using (request)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(subscription.WebhookTimeoutSeconds));
            try
            {
                using var response = await httpClients.CreateClient(HttpClientName)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                result = WebhookResponseClassifier.FromStatus((int)response.StatusCode);
                if (result.Decision == WebhookDecision.Nack)
                    errorDetail = await ReadSnippetAsync(response, timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // shutting down: the lease expires and the delivery is retried
            }
            catch (OperationCanceledException)
            {
                result = WebhookResponseClassifier.Timeout(subscription.WebhookTimeoutSeconds);
            }
            catch (HttpRequestException ex)
            {
                result = WebhookResponseClassifier.ConnectionError(ex);
            }
        }

        logger.LogInformation("Webhook call for delivery {DeliveryId} attempt {Attempt}: {Outcome} {HttpStatusCode} in {DurationMs} ms",
            delivery.DeliveryId, delivery.Attempt, result.ErrorCode ?? result.Decision.ToString(), result.HttpStatusCode,
            (long)time.GetElapsedTime(started).TotalMilliseconds);

        if (result.EndpointHealthy)
            circuits.RecordSuccess(subscription.SubscriptionId);
        else
            circuits.RecordFailure(subscription.SubscriptionId);

        await SettleAsync(delivery, result, errorDetail);
    }

    private HttpRequestMessage BuildRequest(PushSubscriptionRecord subscription, LeasedDeliveryRecord delivery)
    {
        var body = JsonSerializer.Serialize(delivery.ToDelivery(), JsonSerializerOptions.Web);
        var now = time.GetUtcNow();
        var timestamp = WebhookSignature.Timestamp(now);

        // [Fix 7] During rotation both secrets sign, so receivers that hold either one accept the call.
        var signingSecrets = new List<string> { secrets.Unprotect(subscription.WebhookSecret!) };
        if (subscription.PreviousWebhookSecret is { } previous && subscription.PreviousSecretExpiresAt > now.UtcDateTime)
            signingSecrets.Add(secrets.Unprotect(previous));

        var request = new HttpRequestMessage(HttpMethod.Post, subscription.WebhookUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(WebhookHeaders.DeliveryId, delivery.DeliveryId.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(WebhookHeaders.LockToken, delivery.LockToken.ToString());
        request.Headers.Add(WebhookHeaders.Attempt, delivery.Attempt.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(WebhookHeaders.Timestamp, timestamp);
        request.Headers.Add(WebhookHeaders.Signature, WebhookSignature.CreateHeader(timestamp, body, signingSecrets));
        return request;
    }

    private async Task SettleAsync(LeasedDeliveryRecord delivery, WebhookResult result, string? errorDetail)
    {
        // Settlement uses its own token: a call that finished should be recorded even during shutdown.
        try
        {
            switch (result.Decision)
            {
                case WebhookDecision.Ack:
                    await deliveries.AckAsync(delivery.DeliveryId, delivery.LockToken, appId: null, result.HttpStatusCode);
                    activity.DeliveryChanged(delivery.MessageId, delivery.DeliveryId);
                    break;
                case WebhookDecision.Hold:
                    logger.LogInformation("Delivery {DeliveryId} accepted with 202; the lease stays open for a REST ACK", delivery.DeliveryId);
                    break;
                default:
                    var nack = await deliveries.NackAsync(delivery.DeliveryId, delivery.LockToken, appId: null,
                        new FailureDetails(result.ErrorCode, result.ErrorMessage, errorDetail, result.HttpStatusCode));
                    activity.DeliveryChanged(delivery.MessageId, delivery.DeliveryId);
                    if (nack.DeadLettered)
                        logger.LogWarning("Delivery {DeliveryId} dead-lettered after webhook failure {ErrorCode}", delivery.DeliveryId, result.ErrorCode);
                    else
                        logger.LogInformation("Delivery {DeliveryId} retry scheduled at {NextAvailableAt:o} after {ErrorCode}",
                            delivery.DeliveryId, nack.NextAvailableAt, result.ErrorCode);
                    break;
            }
        }
        catch (BrokerException ex) when (ex.Kind == BrokerErrorKind.LeaseLost)
        {
            logger.LogWarning("Lease on delivery {DeliveryId} was lost before the webhook result could be recorded", delivery.DeliveryId);
        }
    }

    /// <summary>The start of an error response body, for the attempt history. Never the request payload.</summary>
    private static async Task<string?> ReadSnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var buffer = new char[MaxErrorDetailChars];
            var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
            return read == 0 ? null : new string(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
        {
            return null;
        }
    }
}
