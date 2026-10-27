using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PayFlow.Contracts;

namespace PayFlow.ServiceDefaults;

/// <summary>Cross-cutting setup shared by every PayFlow web service: telemetry, graceful shutdown, health endpoints.</summary>
public static class ServiceDefaultsExtensions
{
    public const string ReadyTag = "ready";

    /// <summary>
    /// Exporters are opt-in through configuration, so the same binary runs locally and in Azure:
    /// <list type="bullet">
    /// <item><c>OTEL_EXPORTER_OTLP_ENDPOINT</c>: OTLP (Aspire Dashboard, Jaeger, any collector).</item>
    /// <item><c>APPLICATIONINSIGHTS_CONNECTION_STRING</c>: Azure Monitor / Application Insights.</item>
    /// <item><c>Telemetry:Console=true</c>: print spans to stdout (debugging).</item>
    /// </list>
    /// </summary>
    public static WebApplicationBuilder AddPayFlowServiceDefaults(this WebApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Give in-flight Service Bus handlers and the outbox relay time to finish before the pod is killed.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeScopes = true;           // carries CorrelationId / MessageId into exported logs
            o.IncludeFormattedMessage = true;
        });

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o =>
                    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
                .AddHttpClientInstrumentation()
                .AddSource("Npgsql") // built-in Npgsql tracing: one span per DB command
                .AddSource(TelemetryNames.MessagingSource))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(TelemetryNames.MessagingMeter));

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            otel.UseOtlpExporter();

        if (builder.Configuration.GetValue<bool>("Telemetry:Console"))
            otel.WithTracing(t => t.AddConsoleExporter());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
            otel.UseAzureMonitor();

        return builder;
    }

    /// <summary>
    /// <c>/health/live</c>: the process is up (Kubernetes liveness; no dependencies, so a database blip never restarts the pod).
    /// <c>/health/ready</c>: dependencies tagged "ready" are healthy (Kubernetes readiness).
    /// </summary>
    public static WebApplication MapPayFlowHealthChecks(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains(ReadyTag) });
        return app;
    }
}
