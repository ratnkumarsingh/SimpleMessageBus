using BlazorPublisher.Components;
using MessageBroker.Contracts.Client;
using Samples.Shared;

// dotnet run --project samples/BlazorPublisher   (http://localhost:5082; run "SamplePublisher setup" first)
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddSampleSettings().AddEnvironmentVariables().AddCommandLine(args);
var settings = builder.Configuration.GetSampleSettings();

builder.Services.AddSingleton(settings);
builder.Services.AddBrokerClient(o =>
{
    o.BaseAddress = settings.BrokerUrl;
    o.ApiKey = string.IsNullOrEmpty(settings.BlazorPublisher.ApiKey)
        ? throw new InvalidOperationException("No Blazor publisher key. Run 'SamplePublisher setup --AdminKey <key>' first.")
        : settings.BlazorPublisher.ApiKey;
});
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
#if NET10_0_OR_GREATER
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
#else
app.UseStatusCodePagesWithReExecute("/not-found");
#endif
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
