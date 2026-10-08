using MessageBroker.Domain;
using Microsoft.Data.SqlClient;

namespace MessageBroker.Infrastructure.Data;

/// <summary>
/// Turns SQL Server errors into <see cref="BrokerException"/>s: the procedures' 50xxx business errors,
/// unique-key violations as conflicts, and connection/timeout failures as Unavailable (503).
/// Anything else is left alone and surfaces as a 500.
/// </summary>
public static class SqlErrorMapper
{
    // Timeout, network and availability errors where a retry may succeed. 1205 is a deadlock victim.
    private static readonly HashSet<int> TransientNumbers =
        [-2, 2, 53, 121, 233, 1205, 4060, 10053, 10054, 10060, 10061, 11001, 40197, 40501, 40613];

    public static BrokerException? Map(int number, string message, Exception? inner = null)
    {
        if (SqlErrorNumbers.ToKind(number) is { } kind)
            return new BrokerException(kind, message, inner);
        if (number is 2601 or 2627)
            return new BrokerException(BrokerErrorKind.Conflict, "The resource already exists.", inner);
        if (TransientNumbers.Contains(number))
            return new BrokerException(BrokerErrorKind.Unavailable, "The message store is unavailable. Retry later.", inner);
        return null;
    }

    public static BrokerException? Map(SqlException ex) => Map(ex.Number, ex.Message, ex);
}
