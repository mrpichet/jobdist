using JobDist.Core.DAO;
using JobDist.Core.Observability;
using JobDist.Core.Options;
using JobDist.Core.Security;
using JobDist.Core.Services;
using JobDist.Infrastructure.Alerts;
using JobDist.Infrastructure.Data;
using JobDist.Infrastructure.Data.InMemory;
using JobDist.Infrastructure.Messaging;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using JobDist.Infrastructure.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JobDist.Infrastructure;

/// <summary>DI registration of the whole JobDist server-side business layer.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers options, DAO/ADO for the configured provider, services, alerting and (optionally) background workers.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Configuration root.</param>
    /// <param name="runBackgroundWorkers">false for short-lived hosts (stdio MCP server) that must not schedule.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddJobDist(this IServiceCollection services, IConfiguration configuration, bool runBackgroundWorkers = true)
    {
        // Step 1: validated options (fail fast at start-up on invalid configuration).
        services.AddOptions<JobDistOptions>().Bind(configuration.GetSection(JobDistOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<AnomalyOptions>().Bind(configuration.GetSection(AnomalyOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName)).ValidateDataAnnotations()
            .Validate(o => o.Provider == DatabaseProvider.InMemory || !string.IsNullOrWhiteSpace(o.ConnectionString), "Database:ConnectionString is required.").ValidateOnStart();
        services.AddOptions<CryptoOptions>().Bind(configuration.GetSection(CryptoOptions.SectionName));
        services.AddOptions<MessageHubAlertOptions>().Bind(configuration.GetSection(MessageHubAlertOptions.SectionName)).ValidateDataAnnotations();
        services.AddOptions<AlertOptions>().Bind(configuration.GetSection(AlertOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<MessagingOptions>().Bind(configuration.GetSection(MessagingOptions.SectionName));
        services.AddOptions<RabbitMqOptions>().Bind(configuration.GetSection(RabbitMqOptions.SectionName)).ValidateDataAnnotations();
        services.TryAddSingleton(TimeProvider.System);

        // Step 2: data access for the selected provider (all ADO classes are stateless singletons).
        var provider = configuration.GetValue(DatabaseOptions.SectionName + ":Provider", DatabaseProvider.SqlServer);
        services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
        services.AddSingleton<DatabaseInitializer>();
        if (provider == DatabaseProvider.InMemory)
        {
            services.AddSingleton<InMemoryStore>();
            services.AddSingleton<IJobDAO, InMemoryJobADO>();
            services.AddSingleton<IJobAttemptDAO, InMemoryJobAttemptADO>();
            services.AddSingleton<IWorkerDAO, InMemoryWorkerADO>();
            services.AddSingleton<IWorkerCommandDAO, InMemoryWorkerCommandADO>();
            services.AddSingleton<ILeaderLeaseDAO, InMemoryLeaderLeaseADO>();
            services.AddSingleton<IAlertRecipientDAO, InMemoryAlertRecipientADO>();
            services.AddSingleton<IAlertOutboxDAO, InMemoryAlertOutboxADO>();
            services.AddSingleton<IQueueThresholdDAO, InMemoryQueueThresholdADO>();
            services.AddSingleton<IIdempotencyRecordDAO, InMemoryIdempotencyRecordADO>();
            services.AddSingleton<IJobReportDAO, InMemoryJobReportADO>();
        }
        else
        {
            services.AddSingleton<IJobDAO, JobADO>();
            services.AddSingleton<IJobAttemptDAO, JobAttemptADO>();
            services.AddSingleton<IWorkerDAO, WorkerADO>();
            services.AddSingleton<IWorkerCommandDAO, WorkerCommandADO>();
            services.AddSingleton<ILeaderLeaseDAO, LeaderLeaseADO>();
            services.AddSingleton<IAlertRecipientDAO, AlertRecipientADO>();
            services.AddSingleton<IAlertOutboxDAO, AlertOutboxADO>();
            services.AddSingleton<IQueueThresholdDAO, QueueThresholdADO>();
            services.AddSingleton<IIdempotencyRecordDAO, IdempotencyRecordADO>();
            services.AddSingleton<IJobReportDAO, JobReportADO>();
        }

        // Step 3: pure business services (stateless or internally synchronised → singletons).
        services.AddMetrics();
        services.AddSingleton<JobDistMetrics>();
        services.AddSingleton<PqcSignatureService>();
        services.AddSingleton<PayloadProtectionService>();
        services.AddSingleton<PerformanceLearningService>();
        services.AddSingleton<WorkerSignalService>();
        services.AddSingleton<WorkerReportTracker>();
        services.AddSingleton<RetryPolicyService>();
        services.AddSingleton<LeaderElectionService>();
        services.AddSingleton<JobExecutionService>();
        services.AddSingleton<WorkerRegistryService>();
        services.AddSingleton<DispatchService>();
        services.AddSingleton<SchedulerService>();
        services.AddSingleton<FailoverService>();
        services.AddSingleton<JobService>();
        services.AddSingleton<AnomalyDetectionService>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<JobRiskService>();
        services.AddSingleton<ClusterService>();
        services.AddSingleton<ModelTrainer>();
        services.AddSingleton<AlertDispatchService>();
        services.AddSingleton<AlertOutboxProcessor>();
        services.AddSingleton<AlertRecipientService>();
        services.AddSingleton<PendingQueueCache>();
        services.AddSingleton<QueueThresholdService>();
        services.AddSingleton<IQueueAdmission>(sp => sp.GetRequiredService<QueueThresholdService>());
        services.AddSingleton<IdempotencyService>();
        services.AddSingleton<JobCatalogService>();
        services.AddSingleton<JobReportService>();

        // Step 4: MessageHub client. Sends: timeout + circuit breaker, no retry (POST is not idempotent at MessageHub; the outbox
        // retries with back-off). Reads (channel list): timeout + 3 retries with jitter + circuit breaker.
        services.AddSingleton<IAlertDeliveryClient, MessageHubDeliveryClient>();
        services.AddHttpClient(MessageHubDeliveryClient.SendClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false })
            .RemoveAllLoggers()
            .AddResilienceHandler("messagehub-send", b => b
                .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions { FailureRatio = 0.5, MinimumThroughput = 10, SamplingDuration = TimeSpan.FromSeconds(30), BreakDuration = TimeSpan.FromSeconds(30) })
                .AddTimeout(TimeSpan.FromSeconds(10)));
        services.AddHttpClient(MessageHubDeliveryClient.ReadClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false })
            .RemoveAllLoggers()
            .AddResilienceHandler("messagehub-read", b => b
                .AddRetry(new HttpRetryStrategyOptions { MaxRetryAttempts = 3, BackoffType = DelayBackoffType.Exponential, UseJitter = true, Delay = TimeSpan.FromMilliseconds(500) })
                .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions { FailureRatio = 0.5, MinimumThroughput = 10, SamplingDuration = TimeSpan.FromSeconds(30), BreakDuration = TimeSpan.FromSeconds(30) })
                .AddTimeout(TimeSpan.FromSeconds(5)));

        // Step 5: background workers (leader election decides which instance runs cluster-wide duties).
        services.AddHostedService<StartupInitializer>();
        if (runBackgroundWorkers)
        {
            services.AddHostedService<LeaderElectionWorker>();
            services.AddHostedService<SchedulerWorker>();
            services.AddHostedService<FailoverWorker>();
            services.AddHostedService<AnomalyMonitorWorker>();
            services.AddHostedService<ModelTrainingWorker>();
            services.AddHostedService<HousekeepingWorker>();
            services.AddHostedService<MetricsGaugeWorker>();
            services.AddHostedService<QueueThresholdWorker>();
            services.AddHostedService<ConsoleHousekeepingWorker>();

            // Alert transport (Messaging:Transport): Database = poll the outbox; RabbitMq = outbox relay + broker consumer.
            if (configuration.GetValue(MessagingOptions.SectionName + ":Transport", MessagingTransport.Database) == MessagingTransport.RabbitMq)
            {
                services.AddSingleton<RabbitMqConnection>();
                services.AddHostedService<RabbitMqOutboxRelayWorker>();
                services.AddHostedService<RabbitMqAlertConsumerWorker>();
            }
            else
            {
                services.AddHostedService<AlertOutboxWorker>();
            }
        }
        return services;
    }
}
