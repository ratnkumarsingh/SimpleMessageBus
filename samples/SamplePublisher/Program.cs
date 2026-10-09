using MessageBroker.Contracts.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SamplePublisher;
using Samples.Shared;

// dotnet run --project samples/SamplePublisher -- setup --AdminKey <key> [--Samples:BrokerUrl http://localhost:5080/]
// dotnet run --project samples/SamplePublisher -- setup-console --AdminKey <key>   (adds only the console samples)
// dotnet run --project samples/SamplePublisher [--Generator:Count 20] [--Generator:FailEvery 5] [--Generator:InvalidEvery 7]
// dotnet run --project samples/SamplePublisher -- relay-once   (sends pending outbox rows and exits: 0 sent, 1 some rejected, 2 rows still pending)
var setup = args is ["setup" or "setup-console", ..];
var consoleOnly = args is ["setup-console", ..];
var relayOnce = args is ["relay-once", ..];
var options = setup || relayOnce ? args[1..] : args;
var builder = Host.CreateApplicationBuilder(options);
// The local file comes after appsettings but before the environment and command line, which still win.
builder.Configuration.AddSampleSettings().AddEnvironmentVariables().AddCommandLine(options);
var settings = builder.Configuration.GetSampleSettings();

if (setup)
{
    var adminKey = builder.Configuration["AdminKey"]
        ?? throw new ArgumentException("Pass the broker's admin key: setup --AdminKey <key>");
    using var admin = SampleSetup.CreateAdminClient(settings.BrokerUrl, adminKey);
    var path = SampleConfiguration.FindLocalFile() ?? SampleSettings.LocalFileName;
    if (consoleOnly)
    {
        // The other samples' keys and subscriptions in the file are kept as they are.
        await SampleSetup.RunConsoleAsync(admin, settings);
        await SampleSetup.WriteAsync(settings, path);
        Console.WriteLine($"Console samples onboarded on topic '{settings.NotificationsTopicName}'. Settings written to {path}");
        return 0;
    }
    await SampleSetup.RunAsync(admin, settings);
    await SampleSetup.WriteAsync(settings, path);
    Console.WriteLine($"Samples onboarded on topic '{settings.TopicName}'. Settings written to {path}");
    return 0;
}

if (string.IsNullOrEmpty(settings.Publisher.ApiKey))
    throw new InvalidOperationException("No publisher API key. Run 'SamplePublisher setup --AdminKey <key>' first.");

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(builder.Configuration.GetSection("Generator").Get<GeneratorOptions>() ?? new GeneratorOptions());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new SampleDatabase(settings.ConnectionString));
builder.Services.AddSingleton<OutboxStore>();
builder.Services.AddBrokerClient(o =>
{
    o.BaseAddress = settings.BrokerUrl;
    o.ApiKey = settings.Publisher.ApiKey;
});

if (relayOnce)
{
    // One pass for a scheduler (ActiveBatch, SQL Agent, Task Scheduler): no generator, no schema deployment.
    builder.Services.AddSingleton<OutboxRelay>();
    builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning); // keep the job log to the relay's own lines
    using var once = builder.Build();
    var pass = await once.Services.GetRequiredService<OutboxRelay>().DrainAsync(CancellationToken.None);
    Console.WriteLine($"relay-once: {pass.Sent} sent, {pass.Rejected} rejected{(pass.Faulted ? ", stopped early: rows left pending for the next run (see the warning above)" : "")}");
    return OutboxRelay.ExitCode(pass);
}

builder.Services.AddHostedService<PaymentGenerator>();
builder.Services.AddHostedService<OutboxRelay>();

var host = builder.Build();
await host.Services.GetRequiredService<SampleDatabase>().DeployAsync();
await host.RunAsync();
return 0;
