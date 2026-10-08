using MessageBroker.Application.Persistence;
using MessageBroker.Application.Security;
using MessageBroker.Infrastructure.Data;
using MessageBroker.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MessageBroker.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Repositories over ConnectionStrings:BrokerDb, and the webhook secret protector.</summary>
    public static IServiceCollection AddBrokerInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => new BrokerDbOptions
        {
            ConnectionString = configuration.GetConnectionString("BrokerDb")
                ?? throw new InvalidOperationException("ConnectionStrings:BrokerDb is not configured."),
        });
        services.AddSingleton<Db>();
        services.AddSingleton<IApplicationRepository, ApplicationRepository>();
        services.AddSingleton<IAllowedHostRepository, AllowedHostRepository>();
        services.AddSingleton<ITopicRepository, TopicRepository>();
        services.AddSingleton<ISubscriptionRepository, SubscriptionRepository>();
        services.AddSingleton<IMessageRepository, MessageRepository>();
        services.AddSingleton<IDeliveryRepository, DeliveryRepository>();
        services.AddSingleton<IOperationsRepository, OperationsRepository>();

        // Webhook secrets are encrypted with these keys. Without a keys directory, a service account
        // with no user profile keeps them in memory only, and a restart makes every stored secret
        // unreadable (deliveries fail with SigningFailed).
        var dataProtection = services.AddDataProtection().SetApplicationName("MessageBroker");
        if (configuration["Broker:DataProtection:KeysDirectory"] is { Length: > 0 } keysDirectory)
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
            if (OperatingSystem.IsWindows())
                dataProtection.ProtectKeysWithDpapi();
        }
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        return services;
    }
}
