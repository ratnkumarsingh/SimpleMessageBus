namespace MessageBroker.Contracts;

/// <summary>The "type" member of the broker's RFC 9457 problem details, one per error class.</summary>
public static class ProblemTypes
{
    public const string Validation = "urn:message-broker:problem:validation";
    public const string PayloadTooLarge = "urn:message-broker:problem:payload-too-large";
    public const string Unauthorized = "urn:message-broker:problem:unauthorized";
    public const string Forbidden = "urn:message-broker:problem:forbidden";
    public const string NotFound = "urn:message-broker:problem:not-found";
    public const string Conflict = "urn:message-broker:problem:conflict";
    /// <summary>410: the lease was lost. Do not retry the ACK; the delivery will be redelivered.</summary>
    public const string LeaseLost = "urn:message-broker:problem:lease-lost";
    /// <summary>503: the message store is unavailable. Publishers retry with the same Idempotency-Key.</summary>
    public const string Unavailable = "urn:message-broker:problem:unavailable";
}
