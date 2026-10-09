using MessageBroker.Contracts.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Samples.Shared;

// dotnet run --project samples/PullSubscriber
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddSampleSettings().AddEnvironmentVariables().AddCommandLine(args);
var settings = builder.Configuration.GetSampleSettings();

builder.Services.AddPaymentSubscriber(settings, settings.Pull, subscriber: "pull");
builder.Services.AddHostedService<PullPaymentWorker>();

var host = builder.Build();
await host.Services.GetRequiredService<SampleDatabase>().DeployAsync();
await host.RunAsync();

/// <summary>
/// Long-polls the payments pull subscription (spec section 8.4) and hands each delivery to the
/// <see cref="PaymentHandler"/>; <see cref="PullSubscriberWorker"/> does the receiving and settling.
/// </summary>
public sealed class PullPaymentWorker(SampleSettings settings, BrokerClient broker, PaymentHandler handler, ILogger<PullPaymentWorker> logger)
    : PullSubscriberWorker(broker, settings.Pull, handler.HandleAsync, logger);
