using System.Security.Claims;
using System.Text.Encodings.Web;
using MessageBroker.Application.Persistence;
using MessageBroker.Application.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MessageBroker.Api.Auth;

/// <summary>
/// Authenticates "Authorization: ApiKey &lt;key&gt;". On the SignalR hub only, the key may also come as
/// "Bearer &lt;key&gt;" or the access_token query parameter, which is how SignalR clients pass an access
/// token (browser WebSockets cannot set headers).
/// The key is found by its prefix and compared by hash in constant time.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IApplicationRepository applications)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string HubPath = "/hubs";
    private const string HeaderPrefix = ApiKeys.Scheme + " ";
    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = ReadKey();
        if (key is null)
            return AuthenticateResult.NoResult();

        if (!ApiKeys.TryGetPrefix(key, out var prefix))
        {
            Logger.LogWarning("Authentication failed: malformed API key from {RemoteIp}", Context.Connection.RemoteIpAddress);
            return AuthenticateResult.Fail("The API key is invalid.");
        }

        var lookup = await applications.GetKeyByPrefixAsync(prefix, Context.RequestAborted);
        if (lookup is null || !ApiKeys.Verify(key, lookup.Hash))
        {
            Logger.LogWarning("Authentication failed for key {KeyPrefix} from {RemoteIp}", prefix, Context.Connection.RemoteIpAddress);
            return AuthenticateResult.Fail("The API key is invalid, inactive or expired.");
        }

        var claims = new List<Claim>
        {
            new(BrokerClaims.AppId, lookup.AppId.ToString()),
            new(ClaimTypes.Name, lookup.AppName),
            new(BrokerClaims.KeyId, lookup.KeyId.ToString()),
        };
        if (lookup.IsAdmin)
            claims.Add(new Claim(ClaimTypes.Role, BrokerClaims.AdminRole));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = ApiKeys.Scheme;
        return Task.CompletedTask;
    }

    private string? ReadKey()
    {
        var onHub = Request.Path.StartsWithSegments(HubPath);
        var header = Request.Headers.Authorization.ToString();
        if (header.Length > 0)
        {
            if (header.StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase))
                return header[HeaderPrefix.Length..].Trim();
            // SignalR clients send their access token as a bearer token on non-WebSocket transports.
            if (onHub && header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
                return header[BearerPrefix.Length..].Trim();
            return "";
        }

        if (onHub && Request.Query.TryGetValue("access_token", out var token))
            return token.ToString();

        return null;
    }
}

public static class BrokerClaims
{
    public const string AppId = "broker:app_id";
    public const string KeyId = "broker:key_id";
    public const string AdminRole = "Admin";

    public static Caller ToCaller(this ClaimsPrincipal user) => new(
        Guid.Parse(user.FindFirstValue(AppId) ?? throw new InvalidOperationException("The caller is not authenticated.")),
        user.Identity?.Name ?? "",
        user.IsInRole(AdminRole));
}
