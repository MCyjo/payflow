using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PayFlow.Messaging;
using PayFlow.OrderService.Application;
using PayFlow.OrderService.Infrastructure;
using PayFlow.OrderService.Infrastructure.Persistence;
using PayFlow.PaymentService.Application;
using PayFlow.PaymentService.Infrastructure;
using PayFlow.PaymentService.Infrastructure.Persistence;

namespace PayFlow.IntegrationTests.Support;

/// <summary>Builds the services' real DI graphs against a real database, without starting any Service Bus consumer.</summary>
internal static class TestServices
{
    // Never contacted: the Service Bus client is created lazily and these tests replace the publisher.
    private const string UnusedServiceBus = "Endpoint=sb://unused.invalid;SharedAccessKeyName=k;SharedAccessKey=v;UseDevelopmentEmulator=true;";

    private sealed class FixedCorrelation : ICorrelationContext
    {
        public string CorrelationId => "integration-test";
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ServiceBus:ConnectionString"] = UnusedServiceBus })
            .Build();

    public static async Task<ServiceProvider> OrderServicesAsync(string connectionString, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ICorrelationContext, FixedCorrelation>();
        services.AddScoped<CreateOrderHandler>();
        services.AddScoped<CancelOrderHandler>();
        services.AddScoped<GetOrderHandler>();
        services.AddOrderInfrastructure(Config(("ConnectionStrings:Orders", connectionString)));
        services.RemoveAll<IHostedService>();       // no consumer, no relay unless a test asks for it
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Database.MigrateAsync();
        return provider;
    }

    public static async Task<ServiceProvider> PaymentServicesAsync(
        string connectionString, IPaymentProvider? provider = null, Action<IServiceCollection>? configure = null, bool migrate = true, string providerBaseUrl = "http://simulator.test")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddPaymentInfrastructure(Config(
            ("ConnectionStrings:Payments", connectionString),
            ("PaymentProvider:BaseUrl", providerBaseUrl)));
        services.RemoveAll<IHostedService>();
        if (provider is not null)
        {
            services.RemoveAll<IPaymentProvider>();
            services.AddSingleton(provider);
        }
        configure?.Invoke(services);

        var built = services.BuildServiceProvider();
        if (!migrate) return built;

        await using var scope = built.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PaymentDbContext>().Database.MigrateAsync();
        return built;
    }

    /// <summary>Runs only the outbox relay (the one hosted service these tests care about) with a fake publisher.</summary>
    public static async Task<IHost> OrderRelayAsync(string connectionString, IEventPublisher publisher)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ICorrelationContext, FixedCorrelation>();
        builder.Services.AddOrderInfrastructure(Config(("ConnectionStrings:Orders", connectionString)));
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.AddOutbox<OrderDbContext>();
        builder.Services.RemoveAll<IEventPublisher>();
        builder.Services.AddSingleton(publisher);

        var host = builder.Build();
        await host.StartAsync();
        return host;
    }
}

/// <summary>Stands in for Service Bus: records what the relay publishes and lets a test script failures.</summary>
internal sealed class RecordingPublisher : IEventPublisher
{
    private readonly ConcurrentQueue<Guid> _published = new();
    private int _calls;

    /// <summary>Called before recording; return normally to succeed, throw to fail (attempt is 1-based per message).</summary>
    public Func<OutboxMessage, int, Task>? Behaviour { get; init; }
    public TimeSpan Latency { get; init; }

    public IReadOnlyCollection<Guid> Published => _published;

    private readonly ConcurrentDictionary<Guid, int> _attempts = new();

    public async Task PublishAsync(OutboxMessage message, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        var attempt = _attempts.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
        if (Latency > TimeSpan.Zero) await Task.Delay(Latency, ct);
        if (Behaviour is not null) await Behaviour(message, attempt);
        _published.Enqueue(message.Id);
    }
}
