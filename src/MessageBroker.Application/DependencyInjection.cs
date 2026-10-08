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
        services.AddSingleton<DeliveryService>();
        services.AddSingleton<PublishService>();
        services.AddSingleton<MessageQueryService>();
        services.AddSingleton<TopologyService>();
        services.AddSingleton<AdminService>();
        return services;
    }
}
