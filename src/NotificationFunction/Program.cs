using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

// Application Insights wiring is added with the observability work (phase 4).
builder.Build().Run();
