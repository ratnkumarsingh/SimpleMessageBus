namespace MessageBroker.Domain;

public enum BrokerErrorKind
{
    Validation,
    PayloadTooLarge,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    LeaseLost,
    Unavailable,
}

/// <summary>
/// Error numbers raised with THROW by the stored procedures. Kept in one place so the SQL scripts,
/// the mapper and the tests agree.
/// </summary>
public static class SqlErrorNumbers
{
    public const int Validation = 50400;
    public const int Forbidden = 50403;
    public const int NotFound = 50404;
    public const int Conflict = 50409;
    public const int LeaseLost = 50410;

    public static BrokerErrorKind? ToKind(int number) => number switch
    {
        Validation => BrokerErrorKind.Validation,
        Forbidden => BrokerErrorKind.Forbidden,
        NotFound => BrokerErrorKind.NotFound,
        Conflict => BrokerErrorKind.Conflict,
        LeaseLost => BrokerErrorKind.LeaseLost,
        _ => null,
    };
}

public record ValidationError(string Field, string Message);

public class BrokerException(BrokerErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public BrokerErrorKind Kind { get; } = kind;

    public static BrokerException NotFound(string what) => new(BrokerErrorKind.NotFound, $"{what} was not found.");
    public static BrokerException Forbidden(string message = "The application is not permitted to perform this operation.") =>
        new(BrokerErrorKind.Forbidden, message);
    public static BrokerException LeaseLost() =>
        new(BrokerErrorKind.LeaseLost, "The lease was lost: the lock token is stale or the lease has expired.");
}

public sealed class BrokerValidationException(IReadOnlyList<ValidationError> errors)
    : BrokerException(BrokerErrorKind.Validation, "One or more validation errors occurred.")
{
    public IReadOnlyList<ValidationError> Errors { get; } = errors;

    public static void ThrowIfAny(IReadOnlyList<ValidationError> errors)
    {
        if (errors.Count > 0)
            throw new BrokerValidationException(errors);
    }
}
