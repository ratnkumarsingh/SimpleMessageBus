using MessageBroker.Domain;

namespace MessageBroker.Application.Security;

/// <summary>The authenticated application making a call.</summary>
public sealed record Caller(Guid AppId, string Name, bool IsAdmin)
{
    public void RequireAdmin()
    {
        if (!IsAdmin)
            throw BrokerException.Forbidden("This operation requires the Admin role.");
    }
}

/// <summary>Stores webhook secrets encrypted at rest (ASP.NET Core Data Protection in production).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedText);
}
