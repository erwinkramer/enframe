using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Enframe.Mcp;

/// <summary>
/// OpenTelemetry configuration: telemetry for inbound MCP requests, the MCP SDK's
/// per-request activities and histograms, and outbound HTTP calls to the target
/// API. The OTLP exporter sends signals to the collector configured via the
/// standard OTEL_EXPORTER_OTLP_ENDPOINT variable; the service name comes from
/// the standard OTEL_SERVICE_NAME variable, defaulting to "enframe".
/// </summary>
static class Telemetry
{
    public static void AddTelemetry(this IServiceCollection services) =>
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "enframe"))
            .UseOtlpExporter() // all signals, endpoint from OTEL_EXPORTER_OTLP_* env vars
            .WithTracing(tracing => tracing
                .AddSource("Microsoft.AspNetCore") // inbound HTTP requests
                .AddSource("Experimental.ModelContextProtocol") // MCP request spans from the SDK
                .AddHttpClientInstrumentation()) // outbound calls to the target API
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation() // built-in http.server.* metrics (native on .NET 9+)
                .AddMeter("Experimental.ModelContextProtocol")) // MCP operation duration histograms
            .WithLogging(logging => { }, options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
            });
}
