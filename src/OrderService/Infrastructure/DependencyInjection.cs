using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PayFlow.Messaging;
using PayFlow.OrderService.Application;
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
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();

        services.AddServiceBusMessaging(config);
        services.AddOutbox<OrderDbContext>();
        return services;
    }
}
