using MessageBroker.Dashboard;
using MessageBroker.Dashboard.Components;
using MessageBroker.Dashboard.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

// dotnet run --project src/MessageBroker.Dashboard   (http://localhost:5090; sign in with a broker Admin API key)
var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DashboardOptions>(builder.Configuration.GetSection(DashboardOptions.SectionName));
var options = builder.Configuration.GetSection(DashboardOptions.SectionName).Get<DashboardOptions>() ?? new DashboardOptions();

// The sign-in cookie carries the operator's API key, encrypted with these keys. Without a keys
// directory they live in the user profile (or in memory), and a restart may sign everyone out.
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("MessageBroker.Dashboard");
if (options.DataProtectionKeysDirectory is { Length: > 0 } keysDirectory)
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
    if (OperatingSystem.IsWindows())
        dataProtection.ProtectKeysWithDpapi();
}

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "broker-dashboard";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(options.SessionHours);
        o.SlidingExpiration = true;
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/login";
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddBrokerAccess();
builder.Services.AddScoped<BrokerLiveFeed>();
builder.Services.AddScoped<ILiveFeed>(sp => sp.GetRequiredService<BrokerLiveFeed>());
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// A form POST (with the antiforgery token), so another site cannot sign an operator out.
app.MapPost("/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/login");
});

app.Run();

/// <summary>Entry point; public so tests can host the dashboard.</summary>
public partial class Program;
