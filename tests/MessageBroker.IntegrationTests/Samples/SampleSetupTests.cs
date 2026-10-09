using System.Text.Json;
using MessageBroker.Contracts.Client;
using MessageBroker.Contracts.Models;
using MessageBroker.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Configuration;
using SamplePublisher;
using Samples.Shared;

namespace MessageBroker.IntegrationTests.Samples;

public sealed class SampleSetupTests(SqlServerFixture sql) : ApiTest(sql)
{
    [Fact(DisplayName = "S03 Sample setup onboards the samples with the broker's default settings, and its settings file round-trips")]
    public async Task S03_SetupOnboardsSamples()
    {
        var settings = new SampleSettings { TopicName = "payments", Webhook = { Url = $"https://{ApiFactory.SeededHost}" }, ConsoleWebhook = { Url = $"https://{ApiFactory.SeededHost}" } };

        await SampleSetup.RunAsync(Admin, settings);

        var modes = await QueryAsync<(Guid, string, int, int)>(
            "SELECT s.SubscriptionId, s.DeliveryMode, s.WebhookTimeoutSeconds, s.LockDurationSeconds FROM broker.Subscriptions s JOIN broker.Topics t ON t.TopicId = s.TopicId WHERE t.Name = 'payments' ORDER BY s.DeliveryMode", r => (Col<Guid>(r, 0), Col<string>(r, 1), Col<int>(r, 2), Col<int>(r, 3)));
        Assert.Equal(["Pull", "SignalR", "Webhook"], modes.Select(m => m.Item2));
        Assert.All(modes.Where(m => m.Item2 == "Webhook"), m => Assert.True(m.Item3 < m.Item4));
        Assert.Single(settings.Webhook.Secrets);
        Assert.NotEmpty(settings.BlazorPublisher.ApiKey);
        Assert.NotEqual(Guid.Empty, settings.BlazorSubscriber.SubscriptionId);
        Assert.Equal($"https://{ApiFactory.SeededHost}", settings.Webhook.Url);

        // The publisher key can publish, and the pull subscriber's key can receive what it published.
        var publisher = BrokerClient.Create(Api.CreateClient(), settings.Publisher.ApiKey);
        var published = await publisher.PublishAsync("payments", new PublishRequest
        {
            MessageType = "PaymentReceived",
            Payload = JsonSerializer.SerializeToElement(new { paymentId = "PAY-1", amount = 10 }),
        });
        Assert.Equal(3, published.DeliveryCount);
        var pull = BrokerClient.Create(Api.CreateClient(), settings.Pull.ApiKey);
        Assert.Single(await pull.ReceiveAsync(settings.Pull.SubscriptionId));

        var path = Path.Combine(Path.GetTempPath(), $"samples-{Guid.NewGuid():N}.json");
        try
        {
            await SampleSetup.WriteAsync(settings, path);
            var reloaded = new ConfigurationBuilder().AddJsonFile(path).Build().GetSampleSettings();
            Assert.Equal(settings.Pull.SubscriptionId, reloaded.Pull.SubscriptionId);
            Assert.Equal(settings.Webhook.Secrets, reloaded.Webhook.Secrets);
            Assert.Equal(settings.Publisher.ApiKey, reloaded.Publisher.ApiKey);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
