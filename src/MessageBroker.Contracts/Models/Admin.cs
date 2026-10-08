namespace MessageBroker.Contracts.Models;

public sealed record CreateApplicationRequest(string? Name, bool IsAdmin = false);

public sealed record UpdateApplicationRequest(bool? IsActive);

public sealed record ApplicationResponse(Guid AppId, string Name, bool IsAdmin, bool IsActive, DateTime CreatedAt);

public sealed record CreateApiKeyRequest(DateTime? ExpiresAt = null);

public sealed record ApiKeyResponse(Guid KeyId, Guid AppId, string Prefix, bool IsActive, DateTime CreatedAt, DateTime? ExpiresAt)
{
    /// <summary>The full key. Returned only when the key is issued; the broker stores just its hash.</summary>
    public string? ApiKey { get; init; }
}

/// <summary>ResourceType is Topic or Subscription; Permission is Publish, Receive or Manage.</summary>
public sealed record PermissionRequest(string? ResourceType, Guid ResourceId, string? Permission);

public sealed record PermissionResponse(Guid AppId, string ResourceType, Guid ResourceId, string Permission);

public sealed record AllowedHostRequest(string? Host);

public sealed record AllowedHostResponse(string Host, Guid? AddedBy, DateTime AddedAt);
