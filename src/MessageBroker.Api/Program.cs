using MessageBroker.Api.Auth;
using MessageBroker.Api.Errors;
using MessageBroker.Api.Hosting;
using MessageBroker.Api.Hubs;
using MessageBroker.Application;
using MessageBroker.Application.Dispatch;
using MessageBroker.Application.Errors;
using MessageBroker.Application.Security;
using MessageBroker.Contracts;
using MessageBroker.Infrastructure;
using MessageBroker.Worker;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

// Operator commands that run instead of the server (docs/runbook.md):
//   release-script <path>  writes the DBA release script (db/build-release-script.ps1)
//   full-script <path>     writes the full install script with bootstrap data (db/build-release-script.ps1 -Full)
//   new-api-key            prints a new key for Broker:Bootstrap:AdminApiKey
switch (args)
{
    case ["release-script", var releaseScriptPath]:
        File.WriteAllText(releaseScriptPath, MessageBroker.Infrastructure.Data.ReleaseScript.Build());
        Console.WriteLine($"Release script written to {Path.GetFullPath(releaseScriptPath)}");
        return;
    case ["full-script", var fullScriptPath]:
        File.WriteAllText(fullScriptPath, MessageBroker.Infrastructure.Data.ReleaseScript.BuildFull());
        Console.WriteLine($"Full install script written to {Path.GetFullPath(fullScriptPath)}");
        return;
    case ["new-api-key"]:
        Console.WriteLine(ApiKeys.Generate().Key);
        return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSensitiveDataFilters();
builder.Services.AddWindowsService();
builder.Services.AddSystemd();

builder.Services.Configure<BrokerOptions>(builder.Configuration.GetSection(BrokerOptions.SectionName));
builder.Services.AddBrokerApplication();
builder.Services.AddBrokerInfrastructure(builder.Configuration);
builder.Services.AddHostedService<StartupTasks>();
builder.Services.AddBrokerWorker(runLoops: builder.Configuration.GetValue("Broker:Dispatcher:Enabled", true));
builder.Services.AddBrokerHealthChecks();
// Detailed hub errors expose server internals, so only outside production.
builder.Services.AddSignalR(o => o.EnableDetailedErrors = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"));
builder.Services.AddSingleton<IDeliveryPushChannel, HubDeliveryPushChannel>();
// Live activity for admin dashboards on /hubs/admin.
builder.Services.AddBrokerActivityFeed<HubAdminActivitySink>();

// The payload limit is enforced on the parsed payload (413 with a clear message); this body limit only
// stops oversized requests early. JSON escaping can roughly double the size, hence the margin.
var limits = builder.Configuration.GetSection("Broker:Limits").Get<LimitOptions>() ?? new LimitOptions();
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 2L * limits.MaxPayloadBytes + limits.MaxPropertiesBytes + 65_536);

builder.Services.AddAuthentication(ApiKeys.Scheme)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeys.Scheme, null);
builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// Problems the framework raises itself (401 challenge, model binding 400, route 404) get the broker's
// problem types in place of the default RFC 9110 links, so clients can match on one set of types.
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = c =>
{
    var type = c.ProblemDetails.Type;
    if (type is null || type.StartsWith("https://tools.ietf.org/", StringComparison.Ordinal))
        c.ProblemDetails.Type = BrokerProblems.TypeForStatus(c.ProblemDetails.Status ?? c.HttpContext.Response.StatusCode) ?? type;
});
builder.Services.AddExceptionHandler<BrokerExceptionHandler>();
builder.Services.AddControllers();
builder.Services.AddBrokerOpenApi();

var app = builder.Build();

app.UseRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();
// Browser UIs over the OpenAPI document (Swagger at /swagger, Scalar at /scalar); off in production
// unless Broker:ApiDocsUi is true. Swagger UI is middleware, so it goes before authentication.
var apiDocsUi = ApiDocumentation.UiEnabled(app);
if (apiDocsUi)
    app.UseBrokerSwaggerUi();
app.UseAuthentication();
app.UseAppIdLogScope();
app.UseAuthorization();

app.MapControllers();
app.MapHub<DeliveriesHub>(DeliveryHub.Path);
app.MapHub<AdminActivityHub>(AdminHub.Path);
app.MapBrokerHealthChecks();
app.MapOpenApi().AllowAnonymous();
if (apiDocsUi)
    app.MapBrokerScalar();

app.Run();

/// <summary>Entry point; public so the integration tests can host it with WebApplicationFactory.</summary>
public partial class Program;
