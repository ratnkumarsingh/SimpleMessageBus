using MessageBroker.Application.Security;
using Microsoft.AspNetCore.DataProtection;

namespace MessageBroker.Infrastructure.Security;

/// <summary>Encrypts webhook secrets at rest with ASP.NET Core Data Protection (spec section 12).</summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("MessageBroker.WebhookSecret.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedText) => _protector.Unprotect(protectedText);
}
