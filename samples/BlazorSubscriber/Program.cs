using BlazorSubscriber.Components;
using Samples.Shared;

// dotnet run --project samples/BlazorSubscriber   (http://localhost:5083; run "SamplePublisher setup" first)
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddSampleSettings().AddEnvironmentVariables().AddCommandLine(args);
var settings = builder.Configuration.GetSampleSettings();

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<NotificationFeed>();
builder.Services.AddSingleton<NotificationHandler>();
// Joins the notifications SignalR subscription; every delivery lands in the feed, which raises the toast.
builder.Services.AddHostedService(sp => new SignalRSubscriberWorker(settings.BrokerUrl, settings.BlazorSubscriber,
    sp.GetRequiredService<NotificationHandler>().HandleAsync, sp.GetRequiredService<ILogger<SignalRSubscriberWorker>>()));
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
