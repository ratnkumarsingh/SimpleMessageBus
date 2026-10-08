using MessageBroker.Contracts;
using MessageBroker.Domain;

namespace MessageBroker.Application.Errors;

public sealed record ProblemDescription(int Status, string Type, string Title);

/// <summary>HTTP status, problem type and title for each broker error (spec section 11 status codes).</summary>
public static class BrokerProblems
{
    public static ProblemDescription Describe(BrokerErrorKind kind) => kind switch
    {
        BrokerErrorKind.Validation => new(400, ProblemTypes.Validation, "The request is invalid."),
        BrokerErrorKind.PayloadTooLarge => new(413, ProblemTypes.PayloadTooLarge, "The payload is too large."),
        BrokerErrorKind.Unauthorized => new(401, ProblemTypes.Unauthorized, "A valid API key is required."),
        BrokerErrorKind.Forbidden => new(403, ProblemTypes.Forbidden, "The operation is not permitted."),
        BrokerErrorKind.NotFound => new(404, ProblemTypes.NotFound, "The resource was not found."),
        BrokerErrorKind.Conflict => new(409, ProblemTypes.Conflict, "The request conflicts with the current state."),
        BrokerErrorKind.LeaseLost => new(410, ProblemTypes.LeaseLost, "The lease was lost."),
        BrokerErrorKind.Unavailable => new(503, ProblemTypes.Unavailable, "The broker is temporarily unavailable."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Problem type for a status the framework produced itself (401 challenge, 403, 404 route miss).</summary>
    public static string? TypeForStatus(int status) => status switch
    {
        400 => ProblemTypes.Validation,
        401 => ProblemTypes.Unauthorized,
        403 => ProblemTypes.Forbidden,
        404 => ProblemTypes.NotFound,
        409 => ProblemTypes.Conflict,
        410 => ProblemTypes.LeaseLost,
        413 => ProblemTypes.PayloadTooLarge,
        503 => ProblemTypes.Unavailable,
        _ => null,
    };
}
