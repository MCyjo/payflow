using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PayFlow.Contracts;
using PayFlow.Messaging;
using PayFlow.PaymentService.Application;
using PayFlow.PaymentService.Infrastructure.Messaging;
using PayFlow.PaymentService.Infrastructure.Persistence;
using PayFlow.PaymentService.Infrastructure.Provider;

namespace PayFlow.PaymentService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPaymentInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        services.AddDbContext<PaymentDbContext>(o =>
            o.UseNpgsql(config.GetConnectionString("Payments"),
                n => n.MigrationsAssembly(typeof(PaymentDbContext).Assembly.GetName().Name)));

        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddInbox<PaymentDbContext>("payment-service");
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<ProcessPaymentHandler>();
        services.AddScoped<ProviderWebhookHandler>();

        services.AddHttpClient<IPaymentProvider, HttpPaymentProvider>(c =>
                c.BaseAddress = new Uri(config["PaymentProvider:BaseUrl"]
                    ?? throw new InvalidOperationException("Configure PaymentProvider:BaseUrl.")))
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(3);       // a hung provider fails fast...
                o.Retry.MaxRetryAttempts = 3;                             // ...is retried with backoff + jitter...
                o.Retry.Delay = TimeSpan.FromMilliseconds(200);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(15);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30); // ...and a dead one trips the breaker.
            });

        services.AddServiceBusMessaging(config);
        services.AddOutbox<PaymentDbContext>();
        services.AddSubscriptionConsumer<OrderCreatedProcessor>(
            MessagingNames.DomainEventsTopic, MessagingNames.Subscriptions.PaymentService);

        return services;
    }
}
