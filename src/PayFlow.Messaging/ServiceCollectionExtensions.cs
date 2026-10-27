using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PayFlow.Contracts;

namespace PayFlow.Messaging;

public sealed class ServiceBusOptions
{
    public const string Section = "ServiceBus";

    /// <summary>Local emulator / dev only. In Azure leave empty and use FullyQualifiedNamespace + Managed Identity.</summary>
    public string? ConnectionString { get; init; }
    public string? FullyQualifiedNamespace { get; init; }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddServiceBusMessaging(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var options = config.GetSection(ServiceBusOptions.Section).Get<ServiceBusOptions>() ?? new ServiceBusOptions();

        services.AddSingleton(_ => !string.IsNullOrWhiteSpace(options.ConnectionString)
            ? new ServiceBusClient(options.ConnectionString)
            : new ServiceBusClient(
                options.FullyQualifiedNamespace ?? throw new InvalidOperationException(
                    "Configure ServiceBus:ConnectionString (local) or ServiceBus:FullyQualifiedNamespace (Azure)."),
                new DefaultAzureCredential()));
        services.AddSingleton<IEventPublisher, ServiceBusEventPublisher>();
        return services;
    }

    /// <summary>Registers <see cref="IOutbox"/> over the context and the background relay that publishes it.</summary>
    public static IServiceCollection AddOutbox<TContext>(this IServiceCollection services) where TContext : DbContext
    {
        services.AddScoped<IOutbox, EfOutbox<TContext>>();
        services.AddHostedService<OutboxPublisherService<TContext>>();
        return services;
    }

    /// <summary>Registers <see cref="IInbox"/> over the context, scoped to one logical consumer. Requires a TimeProvider.</summary>
    public static IServiceCollection AddInbox<TContext>(this IServiceCollection services, string consumer) where TContext : DbContext
    {
        services.AddScoped<IInbox>(sp => new EfInbox<TContext>(
            sp.GetRequiredService<TContext>(), sp.GetRequiredService<TimeProvider>(), consumer));
        return services;
    }

    public static IServiceCollection AddSubscriptionConsumer<TProcessor>(
        this IServiceCollection services, string topic, string subscription)
        where TProcessor : class, IMessageProcessor
    {
        services.AddScoped<TProcessor>();
        services.AddHostedService(sp => new SubscriptionConsumerService<TProcessor>(
            sp.GetRequiredService<ServiceBusClient>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SubscriptionConsumerService<TProcessor>>>(),
            topic,
            subscription));
        return services;
    }
}
