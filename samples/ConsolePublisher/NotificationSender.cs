using MessageBroker.Contracts.Client;
using Samples.Shared;

namespace ConsolePublisher;

/// <summary>Publishes notifications to the notifications topic, with a new Idempotency-Key each.</summary>
public sealed class NotificationSender(BrokerClient broker, string topicName)
{
    public sealed record Outcome(bool Published, Guid MessageId, int DeliveryCount, string Description);

    public async Task<Outcome> SendAsync(UserNotification notification, CancellationToken ct = default)
    {
        try
        {
            var result = await broker.PublishAsync(topicName, NotificationMessages.ToPublishRequest(notification),
                idempotencyKey: Guid.NewGuid().ToString("N"), ct);
            return new(true, result.MessageId, result.DeliveryCount,
                $"Published {result.MessageId} to {result.DeliveryCount} subscription(s).");
        }
        catch (BrokerApiException ex)
        {
            return new(false, Guid.Empty, 0, $"The broker refused it: {(int)ex.StatusCode} {ex.Title ?? ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return new(false, Guid.Empty, 0, $"The broker is unreachable: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, Guid.Empty, 0, "The broker did not answer in time.");
        }
    }
}
