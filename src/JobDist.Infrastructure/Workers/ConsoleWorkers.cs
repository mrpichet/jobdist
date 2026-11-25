using JobDist.Core.DAO;
using JobDist.Core.Options;
using JobDist.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobDist.Infrastructure.Workers;

/// <summary>
/// Delivers the alert outbox to MessageHub (transport <c>Database</c>). Runs on every instance; rows are claimed with
/// row locks so instances never send the same row twice at the same time.
/// </summary>
public sealed class AlertOutboxWorker(AlertOutboxProcessor processor, IOptions<AlertOptions> options, ILogger<AlertOutboxWorker> logger) : PeriodicWorker(logger)
{
    /// <inheritdoc />
    protected override async Task<TimeSpan> RunCycleAsync(CancellationToken ct)
    {
        var handled = await processor.ProcessOnceAsync(ct).ConfigureAwait(false);

        // A full batch means more may be due: continue at once.
        return handled >= options.Value.OutboxBatchSize ? TimeSpan.Zero : TimeSpan.FromSeconds(options.Value.OutboxPollSeconds);
    }
}

/// <summary>Raises near-limit / over-limit queue alerts (leader only).</summary>
public sealed class QueueThresholdWorker(QueueThresholdService thresholds, LeaderElectionService leader, IOptions<AlertOptions> options, ILogger<QueueThresholdWorker> logger)
    : PeriodicWorker(logger)
{
    /// <inheritdoc />
    protected override async Task<TimeSpan> RunCycleAsync(CancellationToken ct)
    {
        if (leader.IsLeader)
        {
            await thresholds.CheckAndAlertAsync(ct).ConfigureAwait(false);
        }
        return TimeSpan.FromSeconds(options.Value.QueueCheckSeconds);
    }
}

/// <summary>Removes old alert history and expired idempotency records (leader only, hourly).</summary>
public sealed class ConsoleHousekeepingWorker(IAlertOutboxDAO outbox, IdempotencyService idempotency, LeaderElectionService leader, IOptions<AlertOptions> options,
    TimeProvider time, ILogger<ConsoleHousekeepingWorker> logger) : PeriodicWorker(logger)
{
    /// <inheritdoc />
    protected override async Task<TimeSpan> RunCycleAsync(CancellationToken ct)
    {
        if (leader.IsLeader)
        {
            var alerts = await outbox.PurgeAsync(time.GetUtcNow().AddDays(-options.Value.RetentionDays), ct).ConfigureAwait(false);
            var keys = await idempotency.PurgeAsync(ct).ConfigureAwait(false);
            if (alerts + keys > 0)
            {
                logger.LogInformation("Housekeeping removed {Alerts} alert history rows and {Keys} idempotency records.", alerts, keys);
            }
        }
        return TimeSpan.FromHours(1);
    }
}
