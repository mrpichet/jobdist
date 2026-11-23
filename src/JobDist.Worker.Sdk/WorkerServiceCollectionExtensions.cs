using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JobDist.Worker.Sdk;

/// <summary>DI registration for workers.</summary>
public static class WorkerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the JobDist worker runtime (hosted service, API client, metrics) bound to section <c>JobDistWorker</c>.
    /// Add handlers with <see cref="AddJobHandler{THandler}"/>.
    /// </summary>
    /// <param name="services">Services.</param>
    /// <param name="configuration">Configuration root.</param>
    /// <returns>The services (for chaining <c>AddJobHandler</c>).</returns>
    public static IServiceCollection AddJobDistWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Step 1: options with validation at start-up (fail fast on a missing API key).
        services.AddOptions<JobDistWorkerOptions>()
            .Bind(configuration.GetSection(JobDistWorkerOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.ServerUrls.Count > 0 && o.ServerUrls.All(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)),
                "JobDistWorker:ServerUrls must contain absolute http(s) URLs.")
            .ValidateOnStart();

        // Step 2: runtime pieces. No automatic HTTP retry handler: the client fails over itself and the outbox retries.
        services.AddMetrics();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<WorkerMetrics>();
        if (!services.Any(d => d.ServiceType == typeof(JobHandlerRegistry)))
        {
            services.AddSingleton(new JobHandlerRegistry());
        }
        services.AddHttpClient<WorkerApiClient>(c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHostedService<JobDistWorkerService>();
        return services;
    }

    /// <summary>Registers a job handler (scoped: a new instance per job execution).</summary>
    /// <typeparam name="THandler">Handler type.</typeparam>
    /// <param name="services">Services.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddJobHandler<THandler>(this IServiceCollection services) where THandler : class, IJobHandler
    {
        services.AddScoped<THandler>();

        // The registry is filled at registration time; the instance is shared through DI.
        var registry = (JobHandlerRegistry?)services.FirstOrDefault(d => d.ServiceType == typeof(JobHandlerRegistry))?.ImplementationInstance;
        if (registry is null)
        {
            registry = new JobHandlerRegistry();
            services.RemoveAll<JobHandlerRegistry>();
            services.AddSingleton(registry);
        }
        if (!registry.HandlerTypes.Contains(typeof(THandler)))
        {
            registry.HandlerTypes.Add(typeof(THandler));
        }
        return services;
    }
}
