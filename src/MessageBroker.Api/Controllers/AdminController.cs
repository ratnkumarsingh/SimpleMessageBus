using MessageBroker.Application.Services;
using MessageBroker.Contracts.Models;
using Microsoft.AspNetCore.Mvc;

namespace MessageBroker.Api.Controllers;

/// <summary>Applications, API keys, permissions and the webhook host allowlist (Admin).</summary>
[Route("api/v1/admin")]
public sealed class AdminController(AdminService admin) : BrokerController
{
    // ---- applications ----

    [HttpPost("applications")]
    public async Task<ActionResult<ApplicationResponse>> CreateApplication(CreateApplicationRequest request, CancellationToken ct)
    {
        var app = await admin.CreateApplicationAsync(Caller, request, ct);
        return CreatedAtAction(nameof(GetApplication), new { appId = app.AppId }, app);
    }

    [HttpGet("applications")]
    public Task<IReadOnlyList<ApplicationResponse>> ListApplications(CancellationToken ct) => admin.ListApplicationsAsync(Caller, ct);

    [HttpGet("applications/{appId:guid}")]
    public Task<ApplicationResponse> GetApplication(Guid appId, CancellationToken ct) => admin.GetApplicationAsync(Caller, appId, ct);

    /// <summary>Activate or deactivate. Deactivation rejects the application's keys immediately.</summary>
    [HttpPatch("applications/{appId:guid}")]
    public Task<ApplicationResponse> UpdateApplication(Guid appId, UpdateApplicationRequest request, CancellationToken ct) =>
        admin.UpdateApplicationAsync(Caller, appId, request, ct);

    // ---- API keys ----

    /// <summary>Issues a key. The full key is in this response only; at most two keys can be active.</summary>
    [HttpPost("applications/{appId:guid}/keys")]
    public async Task<ActionResult<ApiKeyResponse>> IssueKey(Guid appId, CreateApiKeyRequest? request, CancellationToken ct)
    {
        var key = await admin.IssueKeyAsync(Caller, appId, request ?? new CreateApiKeyRequest(), ct);
        return StatusCode(StatusCodes.Status201Created, key);
    }

    [HttpGet("applications/{appId:guid}/keys")]
    public Task<IReadOnlyList<ApiKeyResponse>> ListKeys(Guid appId, CancellationToken ct) => admin.ListKeysAsync(Caller, appId, ct);

    [HttpDelete("applications/{appId:guid}/keys/{keyId:guid}")]
    public async Task<IActionResult> DeactivateKey(Guid appId, Guid keyId, CancellationToken ct)
    {
        await admin.DeactivateKeyAsync(Caller, appId, keyId, ct);
        return NoContent();
    }

    // ---- permissions ----

    [HttpGet("applications/{appId:guid}/permissions")]
    public Task<IReadOnlyList<PermissionResponse>> ListPermissions(Guid appId, CancellationToken ct) =>
        admin.ListPermissionsAsync(Caller, appId, ct);

    /// <summary>Grants a permission. Granting one the application already holds is a no-op.</summary>
    [HttpPost("applications/{appId:guid}/permissions")]
    public async Task<IActionResult> Grant(Guid appId, PermissionRequest request, CancellationToken ct)
    {
        await admin.GrantAsync(Caller, appId, request, ct);
        return NoContent();
    }

    [HttpPost("applications/{appId:guid}/permissions/revoke")]
    public async Task<IActionResult> Revoke(Guid appId, PermissionRequest request, CancellationToken ct)
    {
        await admin.RevokeAsync(Caller, appId, request, ct);
        return NoContent();
    }

    // ---- webhook host allowlist ----

    [HttpGet("webhook-hosts")]
    public Task<IReadOnlyList<AllowedHostResponse>> ListAllowedHosts(CancellationToken ct) => admin.ListAllowedHostsAsync(Caller, ct);

    [HttpPost("webhook-hosts")]
    public async Task<IActionResult> AddAllowedHost(AllowedHostRequest request, CancellationToken ct)
    {
        await admin.AddAllowedHostAsync(Caller, request, ct);
        return NoContent();
    }

    [HttpDelete("webhook-hosts/{host}")]
    public async Task<IActionResult> RemoveAllowedHost(string host, CancellationToken ct)
    {
        await admin.RemoveAllowedHostAsync(Caller, host, ct);
        return NoContent();
    }
}
