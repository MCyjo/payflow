using System.Reflection;
using NetArchTest.Rules;

namespace PayFlow.ArchitectureTests;

/// <summary>
/// Enforces the layering of each service (Domain -> Application -> Infrastructure -> Api, dependencies point inwards)
/// and the isolation between services, so the structure cannot erode unnoticed.
/// </summary>
public class DependencyRulesTests
{
    private static readonly Assembly OrderDomain = typeof(PayFlow.OrderService.Domain.Order).Assembly;
    private static readonly Assembly OrderApplication = typeof(PayFlow.OrderService.Application.CreateOrderHandler).Assembly;
    private static readonly Assembly OrderInfrastructure = typeof(PayFlow.OrderService.Infrastructure.DependencyInjection).Assembly;
    private static readonly Assembly PaymentDomain = typeof(PayFlow.PaymentService.Domain.Payment).Assembly;
    private static readonly Assembly PaymentApplication = typeof(PayFlow.PaymentService.Application.ProcessPaymentHandler).Assembly;
    private static readonly Assembly PaymentInfrastructure = typeof(PayFlow.PaymentService.Infrastructure.DependencyInjection).Assembly;
    private static readonly Assembly Contracts = typeof(PayFlow.Contracts.OrderCreated).Assembly;
    private static readonly Assembly Messaging = typeof(PayFlow.Messaging.OutboxMessage).Assembly;

    // Technology a pure domain/application layer must not know about.
    private static readonly string[] Technology =
    [
        "Microsoft.EntityFrameworkCore", "Npgsql", "Azure", "Microsoft.AspNetCore", "Polly", "System.Net.Http"
    ];

    public static TheoryData<string, Assembly, string[]> ForbiddenDependencies => new()
    {
        // Domain depends on nothing but the BCL.
        { "Order.Domain", OrderDomain, ["PayFlow.OrderService.Application", "PayFlow.OrderService.Infrastructure", "PayFlow.OrderService.Api", "PayFlow.Contracts", "PayFlow.Messaging", .. Technology] },
        { "Payment.Domain", PaymentDomain, ["PayFlow.PaymentService.Application", "PayFlow.PaymentService.Infrastructure", "PayFlow.PaymentService.Api", "PayFlow.Contracts", "PayFlow.Messaging", .. Technology] },

        // Application depends on Domain and the contracts only (ports are interfaces it owns).
        { "Order.Application", OrderApplication, ["PayFlow.OrderService.Infrastructure", "PayFlow.OrderService.Api", "PayFlow.Messaging", .. Technology] },
        { "Payment.Application", PaymentApplication, ["PayFlow.PaymentService.Infrastructure", "PayFlow.PaymentService.Api", "PayFlow.Messaging", .. Technology] },

        // Infrastructure never reaches up into the web layer.
        { "Order.Infrastructure", OrderInfrastructure, ["PayFlow.OrderService.Api"] },
        { "Payment.Infrastructure", PaymentInfrastructure, ["PayFlow.PaymentService.Api"] },

        // Services are isolated: they talk through events, never through each other's code.
        { "Order.* -> Payment", OrderInfrastructure, ["PayFlow.PaymentService"] },
        { "Order.Application -> Payment", OrderApplication, ["PayFlow.PaymentService"] },
        { "Payment.* -> Order", PaymentInfrastructure, ["PayFlow.OrderService"] },
        { "Payment.Application -> Order", PaymentApplication, ["PayFlow.OrderService"] },

        // Shared libraries stay free of service code, and contracts stay free of infrastructure.
        { "Messaging -> services", Messaging, ["PayFlow.OrderService", "PayFlow.PaymentService"] },
        { "Contracts", Contracts, ["PayFlow.Messaging", "PayFlow.OrderService", "PayFlow.PaymentService", "Microsoft.EntityFrameworkCore", "Azure", "Npgsql"] },
    };

    [Theory]
    [MemberData(nameof(ForbiddenDependencies))]
    public void Layer_DoesNotDependOn_ForbiddenNamespaces(string layer, Assembly assembly, string[] forbidden)
    {
        // TelemetryNames only holds string constants such as "PayFlow.Messaging" (the ActivitySource name).
        // NetArchTest matches that text as if it were a type reference, which is a false positive.
        var result = Types.InAssembly(assembly)
            .That().DoNotHaveName("TelemetryNames")
            .ShouldNot().HaveDependencyOnAny(forbidden)
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"{layer} has forbidden dependencies: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Domain_Entities_HaveNoPublicSetters()
    {
        foreach (var assembly in new[] { OrderDomain, PaymentDomain })
        {
            var result = Types.InAssembly(assembly).That().AreClasses().And().AreNotAbstract()
                .And().DoNotHaveName("DomainException")
                .GetTypes()
                .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                .Where(p => p.SetMethod is { IsPublic: true })
                .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
                .ToList();

            Assert.True(result.Count == 0,
                $"Aggregates must change state through behaviour, not public setters: {string.Join(", ", result)}");
        }
    }

    [Fact]
    public void DomainEvents_AreImmutableRecords()
    {
        var mutable = Contracts.GetTypes()
            .Where(t => typeof(PayFlow.Contracts.IDomainEvent).IsAssignableFrom(t) && t.IsClass)
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p.SetMethod is { IsPublic: true } set
                        && !set.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit"))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .ToList();

        Assert.True(mutable.Count == 0, $"Events must be immutable: {string.Join(", ", mutable)}");
    }
}
