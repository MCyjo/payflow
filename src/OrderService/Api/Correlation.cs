using System.Diagnostics;
using PayFlow.OrderService.Application;

namespace PayFlow.OrderService.Api;

public static class CorrelationHeader
{
    public const string Name = "X-Correlation-Id";
}

/// <summary>Accepts the caller's correlation id or generates one, and echoes it on the response.</summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext ctx)
    {
        var id = ctx.Request.Headers[CorrelationHeader.Name].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100)
            id = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");

        ctx.Items[CorrelationHeader.Name] = id;
        ctx.Response.Headers[CorrelationHeader.Name] = id;
        return next(ctx);
    }
}

internal sealed class HttpCorrelationContext(IHttpContextAccessor accessor) : ICorrelationContext
{
    public string CorrelationId =>
        accessor.HttpContext?.Items[CorrelationHeader.Name] as string ?? Guid.NewGuid().ToString("N");
}
