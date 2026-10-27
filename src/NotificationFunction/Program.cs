using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

// Sends traces, logs and metrics to Application Insights when APPLICATIONINSIGHTS_CONNECTION_STRING is set
// (it is in Azure). Messages carry a Diagnostic-Id, so each invocation joins the trace that started upstream.
builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Build().Run();
