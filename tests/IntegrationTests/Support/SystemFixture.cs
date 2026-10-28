using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Testcontainers.ServiceBus;

namespace PayFlow.IntegrationTests.Support;

/// <summary>
/// The whole system in one process: real PostgreSQL and the real Service Bus emulator in containers, plus the
/// Order API, Payment API and provider simulator hosted in memory and wired to each other.
/// </summary>
public sealed class SystemFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();

    private readonly ServiceBusContainer _serviceBus = new ServiceBusBuilder("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
        .WithAcceptLicenseAgreement(true)
        .WithConfig(Path.Combine(AppContext.BaseDirectory, "servicebus-config.json"))
        .Build();

    private WebApplicationFactory<PayFlow.OrderService.Api.CreateOrderRequest>? _order;
    private WebApplicationFactory<PayFlow.PaymentService.Api.ApiMarker>? _payment;
    private WebApplicationFactory<PayFlow.PaymentProviderSimulator.ChargeRequest>? _simulator;

    public string OrdersDb { get; private set; } = default!;
    public string PaymentsDb { get; private set; } = default!;
    public string ServiceBusConnectionString => _serviceBus.GetConnectionString();
    public HttpClient Order { get; private set; } = default!;
    public HttpClient Payment { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _serviceBus.StartAsync());

        var admin = _postgres.GetConnectionString();
        OrdersDb = await PostgresAdmin.CreateDatabaseAsync(admin, "orders");
        PaymentsDb = await PostgresAdmin.CreateDatabaseAsync(admin, "payments");

        // Environment variables are the one configuration source that Program.cs sees before the host is built.
        Env("ConnectionStrings__Orders", OrdersDb);
        Env("ConnectionStrings__Payments", PaymentsDb);
        Env("ServiceBus__ConnectionString", ServiceBusConnectionString);
        Env("PaymentProvider__BaseUrl", "http://simulator.test");
        Env("Simulator__WebhookUrl", "http://payment.test/api/webhooks/payment");
        Env("Simulator__WebhookDelaySeconds", "1");
        Env("OTEL_SDK_DISABLED", "true");

        _simulator = new WebApplicationFactory<PayFlow.PaymentProviderSimulator.ChargeRequest>()
            .WithWebHostBuilder(b => b.UseEnvironment("Development").ConfigureTestServices(s =>
                // Webhooks leave the simulator through the default HttpClient: route them into the Payment API.
                s.Configure<HttpClientFactoryOptions>(Options.DefaultName, o =>
                    o.HttpMessageHandlerBuilderActions.Add(h => h.PrimaryHandler = _payment!.Server.CreateHandler()))));

        _payment = new WebApplicationFactory<PayFlow.PaymentService.Api.ApiMarker>()
            .WithWebHostBuilder(b => b.UseEnvironment("Development").ConfigureTestServices(s =>
                // The provider client keeps its resilience pipeline; only the network hop is replaced.
                s.Configure<HttpClientFactoryOptions>("IPaymentProvider", o =>
                    o.HttpMessageHandlerBuilderActions.Add(h => h.PrimaryHandler = _simulator.Server.CreateHandler()))));

        _order = new WebApplicationFactory<PayFlow.OrderService.Api.CreateOrderRequest>()
            .WithWebHostBuilder(b => b.UseEnvironment("Development"));

        _ = _simulator.CreateClient();          // builds the host
        Payment = _payment.CreateClient();      // applies migrations, starts the Service Bus consumer + outbox relay
        Order = _order.CreateClient();

        await WarmUpAsync();
    }

    private sealed record OrderStatusDto(Guid Id, string Status);

    /// <summary>
    /// Pushes one order through the whole pipeline before any test runs. The first message after a cold start (emulator
    /// warming up, JIT-cold services, consumers still attaching) can take over a minute on a small CI runner, and that
    /// latency says nothing about the behaviour the tests are written to check. A real failure still surfaces here,
    /// with a message that names the cause.
    /// </summary>
    private async Task WarmUpAsync()
    {
        using var created = await Order.PostAsJsonAsync("/api/orders",
            new { customerId = Guid.NewGuid(), amount = 1m, currency = "EUR", paymentToken = "tok_success" });
        created.EnsureSuccessStatusCode();
        var orderId = (await created.Content.ReadFromJsonAsync<OrderStatusDto>())!.Id;

        await Poll.UntilAsync(
            async () => (await Order.GetFromJsonAsync<OrderStatusDto>($"/api/orders/{orderId}"))!.Status == "Confirmed",
            "the warm-up order to be confirmed (cold start of the emulator and the services)",
            timeoutSeconds: 240);
    }

    public async Task DisposeAsync()
    {
        Order?.Dispose();
        Payment?.Dispose();
        if (_order is not null) await _order.DisposeAsync();
        if (_payment is not null) await _payment.DisposeAsync();
        if (_simulator is not null) await _simulator.DisposeAsync();
        await _serviceBus.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private static void Env(string name, string value) => Environment.SetEnvironmentVariable(name, value);
}
