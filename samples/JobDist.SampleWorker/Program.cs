using JobDist.SampleWorker.Handlers;
using JobDist.Worker.Sdk;

// -----------------------------------------------------------------------------------------------------------------
// Sample worker. Steps:
//   1. Generic host + service defaults (OpenTelemetry → Aspire dashboard when OTEL_EXPORTER_OTLP_ENDPOINT is set).
//   2. AddJobDistWorker binds section "JobDistWorker" (server URLs, API key with the Worker role, concurrency).
//   3. AddJobHandler<T> for every job type this machine can run. The SDK advertises them when it registers.
// Run:  JobDistWorker__ApiKey=dev-worker-key-change-me dotnet run --project samples/JobDist.SampleWorker
// -----------------------------------------------------------------------------------------------------------------
var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

builder.Services
    .AddJobDistWorker(builder.Configuration)
    .AddJobHandler<HashSha256Handler>()
    .AddJobHandler<PrimeCountHandler>()
    .AddJobHandler<ImageResizeHandler>()
    .AddJobHandler<ReportGenerateHandler>();

builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

await builder.Build().RunAsync().ConfigureAwait(false);
