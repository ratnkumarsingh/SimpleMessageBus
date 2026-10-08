using MessageBroker.Application.Persistence;
using MessageBroker.Application.Security;
using MessageBroker.Contracts.Models;
using MessageBroker.Domain;
using Microsoft.Extensions.Logging;

namespace MessageBroker.Application.Services;

/// <summary>Applications, API keys, permissions and the webhook host allowlist. Admin only.</summary>
public sealed class AdminService(
    IApplicationRepository applications,
    IAllowedHostRepository allowedHosts,
    ILogger<AdminService> logger)
{
    // ---- applications ----

    public async Task<ApplicationResponse> CreateApplicationAsync(Caller caller, CreateApplicationRequest request, CancellationToken ct)
    {
        caller.RequireAdmin();
        NameRules.Validate(request.Name, "name");
        var app = await applications.CreateAsync(Guid.CreateVersion7(), request.Name!, request.IsAdmin, ct);
        logger.LogInformation("Application {AppName} ({NewAppId}) registered by {AppId}", app.Name, app.AppId, caller.AppId);
        return app.ToResponse();
    }

    public async Task<IReadOnlyList<ApplicationResponse>> ListApplicationsAsync(Caller caller, CancellationToken ct)
    {
        caller.RequireAdmin();
        return (await applications.ListAsync(ct)).Select(a => a.ToResponse()).ToList();
    }

    public async Task<ApplicationResponse> GetApplicationAsync(Caller caller, Guid appId, CancellationToken ct)
    {
        caller.RequireAdmin();
        return (await applications.GetAsync(appId, ct) ?? throw BrokerException.NotFound("Application")).ToResponse();
    }

    /// <summary>Deactivating an application rejects its keys immediately (usp_ApiKey_GetByPrefix checks it).</summary>
    public async Task<ApplicationResponse> UpdateApplicationAsync(Caller caller, Guid appId, UpdateApplicationRequest request, CancellationToken ct)
    {
        caller.RequireAdmin();
        if (request.IsActive is { } isActive)
        {
            if (!isActive && appId == caller.AppId)
                throw new BrokerValidationException([new("isActive", "An application cannot deactivate itself.")]);
            await applications.SetActiveAsync(appId, isActive, ct);
            logger.LogInformation("Application {TargetAppId} {Change} by {AppId}", appId, isActive ? "activated" : "deactivated", caller.AppId);
        }
        return await GetApplicationAsync(caller, appId, ct);
    }

    // ---- API keys ----

    /// <summary>[Fix 6] Issues a key; at most two can be active at once. The plain key is returned only here.</summary>
    public async Task<ApiKeyResponse> IssueKeyAsync(Caller caller, Guid appId, CreateApiKeyRequest request, CancellationToken ct)
    {
        caller.RequireAdmin();
        if (request.ExpiresAt is { } expiresAt && expiresAt.ToUniversalTime() <= DateTime.UtcNow)
            throw new BrokerValidationException([new("expiresAt", "expiresAt must be in the future.")]);

        var issued = ApiKeys.Generate();
        var key = await applications.CreateKeyAsync(Guid.CreateVersion7(), appId, issued.Prefix, issued.Hash,
            request.ExpiresAt?.ToUniversalTime(), ct);
        logger.LogInformation("API key {KeyPrefix} issued for {TargetAppId} by {AppId}", key.Prefix, appId, caller.AppId);
        return key.ToResponse(issued.Key);
    }

    public async Task<IReadOnlyList<ApiKeyResponse>> ListKeysAsync(Caller caller, Guid appId, CancellationToken ct)
    {
        caller.RequireAdmin();
        return (await applications.ListKeysAsync(appId, ct)).Select(k => k.ToResponse()).ToList();
    }

    public async Task DeactivateKeyAsync(Caller caller, Guid appId, Guid keyId, CancellationToken ct)
    {
        caller.RequireAdmin();
        await applications.DeactivateKeyAsync(appId, keyId, ct);
        logger.LogInformation("API key {KeyId} of {TargetAppId} deactivated by {AppId}", keyId, appId, caller.AppId);
    }

    // ---- permissions ----

    public async Task GrantAsync(Caller caller, Guid appId, PermissionRequest request, CancellationToken ct)
    {
        caller.RequireAdmin();
        var permission = ParsePermission(appId, request);
        await applications.GrantAsync(permission, ct);
        logger.LogInformation("{Permission} on {ResourceType} {ResourceId} granted to {TargetAppId} by {AppId}",
            permission.Permission, permission.ResourceType, permission.ResourceId, appId, caller.AppId);
    }

    public async Task RevokeAsync(Caller caller, Guid appId, PermissionRequest request, CancellationToken ct)
    {
        caller.RequireAdmin();
        var permission = ParsePermission(appId, request);
        await applications.RevokeAsync(permission, ct);
        logger.LogInformation("{Permission} on {ResourceType} {ResourceId} revoked from {TargetAppId} by {AppId}",
            permission.Permission, permission.ResourceType, permission.ResourceId, appId, caller.AppId);
    }

    public async Task<IReadOnlyList<PermissionResponse>> ListPermissionsAsync(Caller caller, Guid appId, CancellationToken ct)
    {
        caller.RequireAdmin();
        return (await applications.ListPermissionsAsync(appId, ct)).Select(p => p.ToResponse()).ToList();
    }

    // ---- webhook host allowlist [Fix 9] ----

    public async Task<IReadOnlyList<AllowedHostResponse>> ListAllowedHostsAsync(Caller caller, CancellationToken ct)
    {
        caller.RequireAdmin();
        return (await allowedHosts.ListAsync(ct)).Select(h => h.ToResponse()).ToList();
    }

    public async Task AddAllowedHostAsync(Caller caller, AllowedHostRequest request, CancellationToken ct)
    {
        caller.RequireAdmin();
        var host = NormalizeHost(request.Host);
        await allowedHosts.AddAsync(host, caller.AppId, ct);
        logger.LogInformation("Webhook host {Host} allowed by {AppId}", host, caller.AppId);
    }

    public async Task RemoveAllowedHostAsync(Caller caller, string host, CancellationToken ct)
    {
        caller.RequireAdmin();
        host = NormalizeHost(host);
        await allowedHosts.RemoveAsync(host, ct);
        logger.LogInformation("Webhook host {Host} removed by {AppId}", host, caller.AppId);
    }

    /// <summary>A bare DNS name or IP address, lower-cased. No scheme, port or path.</summary>
    public static string NormalizeHost(string? host)
    {
        host = host?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(host) || host.Length > 255 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new BrokerValidationException([new("host", "host must be a DNS name or IP address without scheme, port or path.")]);
        return host;
    }

    private static PermissionRecord ParsePermission(Guid appId, PermissionRequest request)
    {
        var errors = new List<ValidationError>();
        if (!EnumNames.TryParse<ResourceType>(request.ResourceType, out var resourceType))
            errors.Add(new("resourceType", "resourceType must be Topic or Subscription."));
        if (!EnumNames.TryParse<Permission>(request.Permission, out var permission))
            errors.Add(new("permission", "permission must be Publish, Receive or Manage."));
        if (request.ResourceId == Guid.Empty)
            errors.Add(new("resourceId", "resourceId is required."));
        BrokerValidationException.ThrowIfAny(errors);

        return new PermissionRecord
        {
            AppId = appId,
            ResourceType = resourceType.ToString(),
            ResourceId = request.ResourceId,
            Permission = permission.ToString(),
        };
    }
}
