using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Samples.Shared;

// dotnet run --project samples/SignalRSubscriber
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddSampleSettings().AddEnvironmentVariables().AddCommandLine(args);
var settings = builder.Configuration.GetSampleSettings();

builder.Services.AddPaymentSubscriber(settings, settings.SignalR, subscriber: "signalr");
builder.Services.AddHostedService<SignalRPaymentWorker>();

var host = builder.Build();
await host.Services.GetRequiredService<SampleDatabase>().DeployAsync();
await host.RunAsync();

/// <summary>Joins the payments SignalR subscription and hands each delivery to the <see cref="PaymentHandler"/>.</summary>
public sealed class SignalRPaymentWorker(SampleSettings settings, PaymentHandler handler, ILogger<SignalRPaymentWorker> logger,
    Action<HttpConnectionOptions>? configureConnection = null)
    : SignalRSubscriberWorker(settings.BrokerUrl, settings.SignalR, handler.HandleAsync, logger, configureConnection);
