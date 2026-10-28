using Microsoft.EntityFrameworkCore;
using PayFlow.OrderService.Api;
using PayFlow.OrderService.Application;
using PayFlow.OrderService.Domain;
using PayFlow.OrderService.Infrastructure;
using PayFlow.OrderService.Infrastructure.Persistence;
using PayFlow.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddPayFlowServiceDefaults("order-service");

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ICorrelationContext, HttpCorrelationContext>();
builder.Services.AddScoped<CreateOrderHandler>();
builder.Services.AddScoped<GetOrderHandler>();
builder.Services.AddScoped<CancelOrderHandler>();
builder.Services.AddOrderInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddDbContextCheck<OrderDbContext>("postgres", tags: [ServiceDefaultsExtensions.ReadyTag]);

var app = builder.Build();

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    // Convenient for a demo (and safe with several replicas: EF takes a database lock while migrating).
    // In production run migrations as a separate deployment step instead and leave the flag off.
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Database.MigrateAsync();
}

app.MapPayFlowHealthChecks();

var orders = app.MapGroup("/api/orders");

orders.MapPost("/", async (CreateOrderRequest req, HttpContext http, CreateOrderHandler handler, CancellationToken ct) =>
{
    var key = http.Request.Headers["Idempotency-Key"].FirstOrDefault();
    try
    {
        var (order, created) = await handler.HandleAsync(
            new CreateOrderCommand(req.CustomerId, req.Amount, req.Currency, req.PaymentToken ?? "tok_success", key), ct);

        return created
            ? Results.Created($"/api/orders/{order.Id}", order)
            : Results.Ok(order); // idempotent replay of an earlier request
    }
    catch (DomainException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["order"] = [ex.Message] });
    }
});

orders.MapGet("/{id:guid}", async (Guid id, GetOrderHandler handler, CancellationToken ct) =>
    await handler.HandleAsync(id, ct) is { } order ? Results.Ok(order) : Results.NotFound());

orders.MapPost("/{id:guid}/cancel", async (Guid id, CancelOrderHandler handler, CancellationToken ct) =>
    await handler.HandleAsync(id, ct) switch
    {
        CancelResult.Cancelled => Results.NoContent(),
        CancelResult.NotFound => Results.NotFound(),
        _ => Results.Conflict(new { error = "Only pending orders can be cancelled." })
    });

app.Run();


public partial class Program;
