using MessageBroker.Contracts.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SamplePublisher;
using Samples.Shared;

// dotnet run --project samples/SamplePublisher -- setup --AdminKey <key> [--Samples:BrokerUrl http://localhost:5080/]
// dotnet run --project samples/SamplePublisher [--Generator:Count 20] [--Generator:FailEvery 5] [--Generator:InvalidEvery 7]
var setup = args is ["setup", ..];
var builder = Host.CreateApplicationBuilder(setup ? args[1..] : args);
// The local file comes after appsettings but before the environment and command line, which still win.
builder.Configuration.AddSampleSettings().AddEnvironmentVariables().AddCommandLine(setup ? args[1..] : args);
var settings = builder.Configuration.GetSampleSettings();

if (setup)
{
    var adminKey = builder.Configuration["AdminKey"]
        ?? throw new ArgumentException("Pass the broker's admin key: setup --AdminKey <key>");
    using var admin = SampleSetup.CreateAdminClient(settings.BrokerUrl, adminKey);
    await SampleSetup.RunAsync(admin, settings);
    var path = SampleConfiguration.FindLocalFile() ?? SampleSettings.LocalFileName;
    await SampleSetup.WriteAsync(settings, path);
    Console.WriteLine($"Samples onboarded on topic '{settings.TopicName}'. Settings written to {path}");
    return;
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
builder.Services.AddHostedService<PaymentGenerator>();
builder.Services.AddHostedService<OutboxRelay>();

var host = builder.Build();
await host.Services.GetRequiredService<SampleDatabase>().DeployAsync();
await host.RunAsync();
