using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Aspire-compatible defaults shared by the server and the workers. When <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set (by the Aspire
/// AppHost, or by docker-compose pointing at the Aspire dashboard container) logs, metrics and traces are pushed over OTLP.
/// </summary>
public static class ServiceDefaultsExtensions
{
    /// <summary>Meter / activity source names exported in addition to ASP.NET Core, HttpClient and runtime instruments.</summary>
    public static readonly string[] JobDistMeters = ["JobDist.Server", "JobDist.Worker"];

    /// <summary>
    /// Adds OpenTelemetry (logs, metrics, traces) and a liveness health check.
    /// </summary>
    /// <typeparam name="TBuilder">Host builder type.</typeparam>
    /// <param name="builder">Host builder.</param>
    /// <returns>The builder.</returns>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        // Step 1: structured logs as OTLP log records.
        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
        });

        // Step 2: metrics (custom JobDist meters + platform instruments) and distributed traces.
        builder.Services.AddOpenTelemetry()
            .WithMetrics(m => m
                .AddMeter(JobDistMeters)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(t => t
                .AddSource(builder.Environment.ApplicationName, "ModelContextProtocol")
                .AddSource(JobDistMeters)
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health") && !ctx.Request.Path.StartsWithSegments("/alive"))
                .AddHttpClientInstrumentation());

        // Step 3: OTLP exporter only when an endpoint is configured (no-op otherwise).
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Step 4: liveness check.
        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        return builder;
    }

    /// <summary>Maps <c>/health</c> (readiness, all checks) and <c>/alive</c> (liveness).</summary>
    /// <param name="app">Web application.</param>
    /// <returns>The application.</returns>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health").AllowAnonymous().DisableRateLimiting();
        app.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = r => r.Tags.Contains("live") }).AllowAnonymous().DisableRateLimiting();
        return app;
    }
}
