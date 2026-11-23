using JobDist.Contracts;
using JobDist.Core.DAO;
using JobDist.Core.Observability;
using JobDist.Core.Options;
using JobDist.Core.Services;
using JobDist.Infrastructure.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobDist.Infrastructure.Workers;

/// <summary>Base for periodic background loops with error isolation (one failed cycle never stops the loop).</summary>
public abstract class PeriodicWorker(ILogger logger) : BackgroundService
{
    /// <summary>Runs one cycle.</summary>
    /// <returns>Delay before the next cycle.</returns>
    protected abstract Task<TimeSpan> RunCycleAsync(CancellationToken ct);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield so host start-up is not blocked by the first cycle.
        await Task.Yield();
        var errors = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = await RunCycleAsync(stoppingToken).ConfigureAwait(false);
                errors = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Exponential back-off on repeated errors (e.g. database fail-over in progress).
                errors++;
                logger.LogError(ex, "{Worker} cycle failed ({Errors} in a row).", GetType().Name, errors);
                delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(errors, 5))));
            }
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}

/// <summary>Initialises the database schema and warms up the ML model from history before other workers start.</summary>
public sealed class StartupInitializer(DatabaseInitializer database, ModelTrainer trainer, ILogger<StartupInitializer> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await trainer.TrainAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Model warm-up failed; starting with an empty model.");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Leader election heartbeat (every TTL/3); releases the lease on shutdown for instant fail-over.</summary>
public sealed class LeaderElectionWorker(LeaderElectionService leader, IOptions<JobDistOptions> options, ILogger<LeaderElectionWorker> logger) : PeriodicWorker(logger)
{
    /// <inheritdoc />
    protected override async Task<TimeSpan> RunCycleAsync(CancellationToken ct)
    {
        await leader.TryAcquireOrRenewAsync(ct).ConfigureAwait(false);
        return TimeSpan.FromSeconds(Math.Max(1, options.Value.LeaderLeaseSeconds / 3.0));
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await leader.ReleaseAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Scheduling loop on the leader: runs back-to-back while there is a backlog, idles briefly when the queue is empty.</summary>
public sealed class SchedulerWorker(SchedulerService scheduler, LeaderElectionService leader, IOptions<JobDistOptions> options, ILogger<SchedulerWorker> logger) : PeriodicWorker(logger)
{
    /// <inheritdoc />
    protected override async Task<TimeSpan> RunCycleAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!leader.IsLeader)
        {
            return TimeSpan.FromSeconds(1);
        }
        var assigned = await scheduler.RunCycleAsync(ct).ConfigureAwait(false);
        return assigned > 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(o.SchedulerIdleDelayMs);
    }
}

/// <summary>Fail-over detection on the leader (every 2 seconds).</summary>
public sealed class FailoverWorker(FailoverService failover, LeaderElectionService leader, ILogger<FailoverWorker> logger) : PeriodicWorker(logger)
{
    /// <inheritdoc />
    protected override async Task<TimeSpan> RunCycleAsync(CancellationToken ct)
    {
        if (leader.IsLeader)
        {
            await failover.RunOnceAsync(ct).ConfigureAwait(false);
        }
        return TimeSpan.FromSeconds(2);
    }
}

/// <summary>Anomaly scan + admin alert on the leader.</summary>
public sealed class AnomalyMonitorWorker(AnomalyDetectionService detector, AlertService alerts, LeaderElectionService leader, IOptions<AnomalyOptions> options,
    ILogger<AnomalyMonitorWorker> logger) : PeriodicWorker(logger)
{
    /// <inheritdoc />
    protected override async Task<TimeSpan> RunCycleAsync(CancellationToken ct)
    {
        if (leader.IsLeader)
        {
            var report = await detector.ScanAsync(null, ct).ConfigureAwait(false);
            if (report.Anomalies.Count > 0)
            {
                await alerts.NotifyAsync(report, false, ct).ConfigureAwait(false);
            }
        }
        return TimeSpan.FromMinutes(options.Value.ScanIntervalMinutes);
    }
}

/// <summary>Re-trains the ML model from stored history (all instances converge to the same model).</summary>
public sealed class ModelTrainer(IJobAttemptDAO attempts, IWorkerDAO workers, PerformanceLearningService model, TimeProvider time, ILogger<ModelTrainer> logger)
{
    /// <summary>Replays the last 7 days (max 200,000 attempts).</summary>
    public async Task TrainAsync(CancellationToken ct)
    {
        var history = await attempts.GetFinishedSinceAsync(time.GetUtcNow().AddDays(-7), 200_000, ct).ConfigureAwait(false);
        var byId = (await workers.ListAsync(true, ct).ConfigureAwait(false)).ToDictionary(w => w.WorkerId);
        var learned = model.TrainFromHistory(history, byId);
        logger.LogInformation("Performance model trained from {Count} attempts.", learned);
    }
}

/// <summary>Periodic model refresh (every 5 minutes).</summary>
public sealed class ModelTrainingWorker(ModelTrainer trainer, ILogger<ModelTrainingWorker> logger) : PeriodicWorker(logger)
{
    /// <inheritdoc />
    protected override async Task<TimeSpan> RunCycleAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        await trainer.TrainAsync(ct).ConfigureAwait(false);
        return TimeSpan.Zero;
    }
}

/// <summary>Deletes old finished jobs on the leader (hourly, in small batches).</summary>
public sealed class HousekeepingWorker(IJobDAO jobs, LeaderElectionService leader, IOptions<JobDistOptions> options, TimeProvider time, ILogger<HousekeepingWorker> logger) : PeriodicWorker(logger)
{
    /// <inheritdoc />
    protected override async Task<TimeSpan> RunCycleAsync(CancellationToken ct)
    {
        if (leader.IsLeader)
        {
            var cutoff = time.GetUtcNow().AddDays(-options.Value.RetentionDays);
            int deleted, total = 0;
            do
            {
                deleted = await jobs.PurgeAsync(cutoff, 1_000, ct).ConfigureAwait(false);
                total += deleted;
            }
            while (deleted == 1_000 && !ct.IsCancellationRequested);
            if (total > 0)
            {
                logger.LogInformation("Housekeeping removed {Count} finished jobs older than {Days} days.", total, options.Value.RetentionDays);
            }
        }
        return TimeSpan.FromHours(1);
    }
}

/// <summary>Refreshes values for observable gauges (queue depth, workers online, leader flag) every 10 seconds.</summary>
public sealed class MetricsGaugeWorker(IJobDAO jobs, IWorkerDAO workers, LeaderElectionService leader, JobDistMetrics metrics, ILogger<MetricsGaugeWorker> logger) : PeriodicWorker(logger)
{
    private long _queue;
    private long _online;

    /// <inheritdoc />
    protected override async Task<TimeSpan> RunCycleAsync(CancellationToken ct)
    {
        metrics.BindGauges(() => Interlocked.Read(ref _queue), () => Interlocked.Read(ref _online), () => leader.IsLeader ? 1 : 0);
        var byStatus = await jobs.CountByStatusAsync(ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _queue, byStatus.GetValueOrDefault(JobStatus.Pending));
        Interlocked.Exchange(ref _online, (await workers.ListAsync(false, ct).ConfigureAwait(false)).Count(w => w.Status == WorkerStatus.Online));
        return TimeSpan.FromSeconds(10);
    }
}
