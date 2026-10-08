using MessageBroker.Application.Dispatch;
using MessageBroker.Worker.Maintenance;
using MessageBroker.Worker.Push;
using MessageBroker.Worker.SignalR;
using MessageBroker.Worker.Webhooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MessageBroker.Worker;

public static class DependencyInjection
{
    /// <summary>
    /// The dispatcher: push channels and the background loops. <paramref name="runLoops"/> false
    /// (Broker:Dispatcher:Enabled) registers the loops without starting them, so tests can run single passes.
    /// </summary>
    public static IServiceCollection AddBrokerWorker(this IServiceCollection services, bool runLoops)
    {
        services.TryAddSingleton(TimeProvider.System);

        // Redirects are never followed (SSRF control, spec section 12); each call has its own timeout.
        services.AddHttpClient(WebhookChannel.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                UseCookies = false,
            });
        services.AddSingleton<CircuitBreakerRegistry>();
        services.AddSingleton<WebhookChannel>();
        services.AddSingleton<IPushChannel>(sp => sp.GetRequiredService<WebhookChannel>());

        // SignalR: the API registers IDeliveryPushChannel (its hub context).
        services.AddSingleton<IConnectionRegistry, ConnectionRegistry>();
        services.AddSingleton<SignalRChannel>();
        services.AddSingleton<IPushChannel>(sp => sp.GetRequiredService<SignalRChannel>());
        services.AddSingleton<IPushStatus, WorkerPushStatus>();

        services.AddSingleton<LeaseLoop>();
        services.AddSingleton<MaintenanceLoop>();
        services.AddSingleton<RetentionLoop>();
        if (runLoops)
        {
            services.AddHostedService(sp => sp.GetRequiredService<LeaseLoop>());
            services.AddHostedService(sp => sp.GetRequiredService<MaintenanceLoop>());
            services.AddHostedService(sp => sp.GetRequiredService<RetentionLoop>());
        }
        return services;
    }
}
