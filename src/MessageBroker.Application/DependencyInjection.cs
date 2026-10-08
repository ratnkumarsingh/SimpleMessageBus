using MessageBroker.Application.Dispatch;
using MessageBroker.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MessageBroker.Application;

public static class DependencyInjection
{
    /// <summary>Use-case services. The host binds <see cref="BrokerOptions"/> from configuration.</summary>
    public static IServiceCollection AddBrokerApplication(this IServiceCollection services)
    {
        services.AddOptions<BrokerOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IDispatcherSignal, DispatcherSignal>();
        services.AddSingleton<LongPoller>();
        services.AddSingleton<IDeliverySettlements, DeliverySettlements>();
        services.TryAddSingleton<IBrokerActivityFeed, NullBrokerActivityFeed>();
        services.TryAddSingleton<IPushStatus, NullPushStatus>();
        services.AddSingleton<DeliveryService>();
        services.AddSingleton<PublishService>();
        services.AddSingleton<MessageQueryService>();
        services.AddSingleton<TopologyService>();
        services.AddSingleton<AdminService>();
        services.AddSingleton<DashboardService>();
        return services;
    }

    /// <summary>
    /// Replaces the no-op activity feed with <see cref="BrokerActivityFeed"/>, which sends to admin
    /// dashboards through the host's <see cref="IAdminActivitySink"/>.
    /// </summary>
    public static IServiceCollection AddBrokerActivityFeed<TSink>(this IServiceCollection services)
        where TSink : class, IAdminActivitySink
    {
        services.AddSingleton<IAdminActivitySink, TSink>();
        services.AddSingleton<BrokerActivityFeed>();
        services.Replace(ServiceDescriptor.Singleton<IBrokerActivityFeed>(sp => sp.GetRequiredService<BrokerActivityFeed>()));
        services.AddHostedService(sp => sp.GetRequiredService<BrokerActivityFeed>());
        return services;
    }
}
