using System.Diagnostics;
using System.Security.Claims;
using MessageBroker.Api.Auth;
using Microsoft.Extensions.Primitives;

namespace MessageBroker.Api.Hosting;

/// <summary>
/// Request logging (spec sections 12 and 13). Every log event written while a request runs carries the
/// caller's AppId as a scope, and one line per request records method, path, status and duration.
/// ASP.NET Core's own request lines print the raw query string, which on the hub holds the API key as
/// <c>access_token</c>, so they are switched off and this line redacts the token instead.
/// </summary>
public static class RequestLogging
{
    public const string HostingCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";
    public const string Redacted = "***";
    private static readonly string[] SecretQueryKeys = ["access_token"];

    /// <summary>MVC logs action arguments, which include message payloads, at Trace.</summary>
    public const string ActionInvokerCategory = "Microsoft.AspNetCore.Mvc.Infrastructure.ControllerActionInvoker";

    /// <summary>
    /// Keeps API keys and payloads out of framework logs: the request lines (raw query string) are held
    /// below Information and the MVC action-argument lines below Trace. Registered after the
    /// configuration rules, so a "Logging:LogLevel" entry for these categories cannot turn them back on.
    /// </summary>
    public static ILoggingBuilder AddSensitiveDataFilters(this ILoggingBuilder logging) => logging
        .AddFilter(HostingCategory, LogLevel.Warning)
        .AddFilter(ActionInvokerCategory, LogLevel.Debug);

    /// <summary>
    /// Logs one line per request. Call first, outside the exception handler, so the line shows the
    /// status the client actually received.
    /// </summary>
    public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app)
    {
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("MessageBroker.Api.Requests");
        return app.Use(async (context, next) =>
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await next(context);
            }
            finally
            {
                using var scope = BeginAppIdScope(logger, context);
                logger.LogInformation("HTTP {Method} {Path}{Query} responded {StatusCode} in {DurationMs} ms",
                    context.Request.Method, context.Request.Path.Value, RedactQuery(context.Request.Query),
                    context.Response.StatusCode, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        });
    }

    /// <summary>Adds the caller's AppId to every log event of the request. Call after UseAuthentication.</summary>
    public static IApplicationBuilder UseAppIdLogScope(this IApplicationBuilder app)
    {
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("MessageBroker.Api.Requests");
        return app.Use(async (context, next) =>
        {
            using var scope = BeginAppIdScope(logger, context);
            await next(context);
        });
    }

    private static IDisposable? BeginAppIdScope(ILogger logger, HttpContext context) =>
        context.User.FindFirstValue(BrokerClaims.AppId) is { } appId
            ? logger.BeginScope(new Dictionary<string, object> { ["AppId"] = appId })
            : null;

    /// <summary>The query string with secret values replaced by <see cref="Redacted"/>.</summary>
    public static string RedactQuery(IQueryCollection query)
    {
        if (query.Count == 0)
            return "";
        var redacted = query.Select(p => new KeyValuePair<string, StringValues>(p.Key,
            SecretQueryKeys.Contains(p.Key, StringComparer.OrdinalIgnoreCase) ? Redacted : p.Value));
        return QueryString.Create(redacted).Value!.Replace("%2A%2A%2A", Redacted, StringComparison.Ordinal);
    }
}
