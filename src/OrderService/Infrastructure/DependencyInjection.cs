using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PayFlow.OrderService.Application;
using PayFlow.OrderService.Infrastructure.Messaging;
using PayFlow.OrderService.Infrastructure.Persistence;

namespace PayFlow.OrderService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddOrderInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<OrderDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Orders"),
                n => n.MigrationsAssembly(typeof(OrderDbContext).Assembly.GetName().Name)));

        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IOutbox, EfOutbox>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();

        var sb = config.GetSection(ServiceBusOptions.Section).Get<ServiceBusOptions>() ?? new ServiceBusOptions();
        services.AddSingleton(_ => !string.IsNullOrWhiteSpace(sb.ConnectionString)
            ? new ServiceBusClient(sb.ConnectionString)
            : new ServiceBusClient(
                sb.FullyQualifiedNamespace ?? throw new InvalidOperationException(
                    "Configure ServiceBus:ConnectionString (local) or ServiceBus:FullyQualifiedNamespace (Azure)."),
                new DefaultAzureCredential()));
        services.AddSingleton<IEventPublisher, ServiceBusEventPublisher>();
        services.AddHostedService<OutboxPublisherService>();

        return services;
    }
}
