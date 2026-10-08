using MessageBroker.Contracts.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Samples.Shared;

public static class SubscriberServices
{
    /// <summary>
    /// Registers what every sample subscriber needs: the settings, the sample database, the invoice
    /// store, the <see cref="PaymentHandler"/> (deduplicating under <paramref name="subscriber"/>) and a
    /// <see cref="BrokerClient"/> with the subscriber's own key. Returns the client's builder so a host
    /// can change its transport.
    /// </summary>
    public static IHttpClientBuilder AddPaymentSubscriber(this IServiceCollection services, SampleSettings settings, SampleApp app, string subscriber)
    {
        services.AddSingleton(settings);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new SampleDatabase(settings.ConnectionString));
        services.AddSingleton<InvoiceStore>();
        services.AddSingleton(sp => new PaymentHandler(sp.GetRequiredService<InvoiceStore>(), subscriber,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<PaymentHandler>(), settings.FailPrefix));
        return services.AddBrokerClient(o =>
        {
            o.BaseAddress = settings.BrokerUrl;
            o.ApiKey = string.IsNullOrEmpty(app.ApiKey)
                ? throw new InvalidOperationException("No API key for this subscriber. Run 'SamplePublisher setup --AdminKey <key>' first.")
                : app.ApiKey;
        });
    }
}
