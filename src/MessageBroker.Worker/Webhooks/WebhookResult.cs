namespace MessageBroker.Worker.Webhooks;

public enum WebhookDecision
{
    /// <summary>2xx (except 202): the response is the acknowledgement.</summary>
    Ack,
    /// <summary>202: the subscriber settles later through the REST API; the lease stays open.</summary>
    Hold,
    /// <summary>Any other status, a timeout or a connection failure: a failed attempt.</summary>
    Nack,
}

public sealed record WebhookResult(WebhookDecision Decision, int? HttpStatusCode, string? ErrorCode = null, string? ErrorMessage = null)
{
    /// <summary>Whether the endpoint answered in a way that should keep its circuit closed.</summary>
    public bool EndpointHealthy => Decision != WebhookDecision.Nack;
}

/// <summary>Maps a webhook call's outcome to ACK, hold or NACK (spec section 9).</summary>
public static class WebhookResponseClassifier
{
    public static WebhookResult FromStatus(int status) => status switch
    {
        202 => new(WebhookDecision.Hold, status),
        >= 200 and < 300 => new(WebhookDecision.Ack, status),
        >= 300 and < 400 => new(WebhookDecision.Nack, status, $"Http{status}", $"The endpoint answered {status}; redirects are not followed."),
        _ => new(WebhookDecision.Nack, status, $"Http{status}", $"The endpoint answered {status}."),
    };

    public static WebhookResult Timeout(int timeoutSeconds) =>
        new(WebhookDecision.Nack, null, "Timeout", $"No response within {timeoutSeconds} s.");

    public static WebhookResult ConnectionError(Exception ex) =>
        new(WebhookDecision.Nack, null, "ConnectionError", ex.Message);
}
