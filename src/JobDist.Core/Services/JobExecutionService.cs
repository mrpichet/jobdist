using JobDist.Contracts;
using JobDist.Core.DAO;
using JobDist.Core.Domain;
using JobDist.Core.Observability;
using JobDist.Core.Options;
using JobDist.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobDist.Core.Services;

/// <summary>
/// Execution lifecycle reported by workers (started / completed) and server-side releases (worker lost, lease expired,
/// unregistered). Every transition records the attempt in the distribution history, feeds the ML model and applies the
/// retry policy. Stale reports (a worker reporting on a job that was already re-distributed) are rejected with 409.
/// </summary>
public sealed class JobExecutionService(
    IJobDAO jobs,
    IJobAttemptDAO attempts,
    IWorkerDAO workers,
    IWorkerCommandDAO commands,
    RetryPolicyService retryPolicy,
    PerformanceLearningService model,
    WorkerReportTracker tracker,
    JobDistMetrics metrics,
    IOptions<JobDistOptions> options,
    TimeProvider time,
    ILogger<JobExecutionService> logger)
{
    private const int MaxOptimisticRetries = 5;

    /// <summary>
    /// Worker reports that it started an attempt.
    /// </summary>
    /// <param name="workerId">Authenticated worker.</param>
    /// <param name="jobId">Job.</param>
    /// <param name="attemptId">Attempt from the assignment.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> when a cancel was already requested (the worker should stop right away).</returns>
    /// <exception cref="ConflictException">The worker no longer owns this attempt (job re-distributed).</exception>
    public async Task<bool> StartedAsync(Guid workerId, Guid jobId, Guid attemptId, CancellationToken ct)
    {
        for (var i = 0; i < MaxOptimisticRetries; i++)
        {
            // Step 1: ownership check (job held by this worker with this attempt).
            var job = await LoadOwnedAsync(workerId, jobId, attemptId, ct).ConfigureAwait(false);
            if (job.Status == JobStatus.Running)
            {
                return false; // duplicate report (worker retried after a network error) - idempotent
            }
            var cancelRequested = job.Status == JobStatus.CancelRequested;

            // Step 2: Assigned → Running; lease now covers timeout + grace.
            var now = time.GetUtcNow();
            if (!cancelRequested)
            {
                job.Status = JobStatus.Running;
            }
            job.StartedUtc = now;
            job.LeaseExpiresUtc = now.AddSeconds(job.TimeoutSeconds + options.Value.LeaseGraceSeconds);
            if (!await jobs.TryUpdateAsync(job, ct).ConfigureAwait(false))
            {
                continue;
            }

            // Step 3: history.
            var attempt = await attempts.GetAsync(attemptId, ct).ConfigureAwait(false);
            if (attempt is not null)
            {
                attempt.StartedUtc = now;
                await attempts.UpdateAsync(attempt, ct).ConfigureAwait(false);
            }
            return cancelRequested;
        }
        throw new ConflictException("Job is being modified concurrently; retry.");
    }

    /// <summary>
    /// Worker reports the end of an attempt.
    /// </summary>
    /// <param name="workerId">Authenticated worker.</param>
    /// <param name="jobId">Job.</param>
    /// <param name="report">Outcome, duration, result / error.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Job status after applying the retry policy.</returns>
    /// <exception cref="ConflictException">Stale report.</exception>
    public async Task<JobStatusResponse> CompletedAsync(Guid workerId, Guid jobId, JobCompletedRequest report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Outcome is AttemptOutcome.InProgress or AttemptOutcome.WorkerLost or AttemptOutcome.Released)
        {
            throw new ValidationException("Outcome must be Succeeded, Failed, TimedOut or Cancelled.");
        }
        var job = await LoadOwnedAsync(workerId, jobId, report.AttemptId, ct).ConfigureAwait(false);
        return await FinishAttemptAsync(job, report.Outcome, report.Retryable, report.DurationMs,
            InputGuard.CleanText(report.Result, options.Value.MaxPayloadBytes), InputGuard.CleanText(report.Error, 4000), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Server-side release of a job a worker can no longer finish (worker lost, lease expired, unregistered, re-registered).
    /// </summary>
    /// <param name="job">Job held by a worker.</param>
    /// <param name="outcome">WorkerLost, Released or TimedOut.</param>
    /// <param name="reason">Error text stored on the attempt.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Status after release (Pending = re-distributed).</returns>
    public async Task<JobStatus> ReleaseAsync(JobDTO job, AttemptOutcome outcome, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var result = await FinishAttemptAsync(job, outcome, true, 0, null, reason, ct).ConfigureAwait(false);
        if (result.Status == JobStatus.Pending)
        {
            metrics.Failover(1);
        }
        return result.Status;
    }

    /// <summary>Common end-of-attempt logic with optimistic concurrency.</summary>
    private async Task<JobStatusResponse> FinishAttemptAsync(JobDTO job, AttemptOutcome outcome, bool retryable, double durationMs,
        string? result, string? error, CancellationToken ct)
    {
        for (var i = 0; ; i++)
        {
            var now = time.GetUtcNow();
            var attemptId = job.CurrentAttemptId;
            var workerId = job.AssignedWorkerId;

            // Step 1: retry policy decides the next state.
            var decision = retryPolicy.Decide(job, outcome, retryable, now);
            job.Status = decision.FinalStatus;
            if (!decision.CountsAsAttempt)
            {
                job.Attempt = Math.Max(0, job.Attempt - 1);
            }
            job.LastDurationMs = durationMs > 0 ? durationMs : job.LastDurationMs;
            if (decision.IsRetry)
            {
                job.NextAttemptUtc = decision.NextAttemptUtc ?? now;
                job.AssignedWorkerId = null;
                job.CurrentAttemptId = null;
                job.LeaseExpiresUtc = null;
                job.AssignedUtc = null;
                job.LastError = error;
                if (outcome != AttemptOutcome.Released)
                {
                    job.LastFailedWorkerId = workerId;
                }
            }
            else
            {
                job.CompletedUtc = now;
                job.LeaseExpiresUtc = null;
                job.Result = outcome == AttemptOutcome.Succeeded ? result : job.Result;
                job.LastError = outcome == AttemptOutcome.Succeeded ? null : error ?? job.LastError;
            }

            // Step 2: conditional update; on a race re-read and re-apply (e.g. cancel arrived at the same moment).
            if (await jobs.TryUpdateAsync(job, ct).ConfigureAwait(false))
            {
                await RecordAttemptAsync(job, attemptId, workerId, outcome, durationMs, error, now, ct).ConfigureAwait(false);
                if (job.IsTerminal)
                {
                    await commands.AcknowledgeJobAsync(job.JobId, now, ct).ConfigureAwait(false);
                    if (job.Status == JobStatus.Cancelled)
                    {
                        metrics.JobCancelled(job.JobType);
                    }
                }
                else
                {
                    metrics.JobRetried(job.JobType, decision.Reason);
                }
                metrics.AttemptCompleted(job.JobType, outcome.ToString(), durationMs);
                logger.LogDebug("Job {JobId} attempt {Attempt} ended {Outcome} → {Status} ({Reason}).", job.JobId, job.Attempt, outcome, job.Status, decision.Reason);
                return job.ToResponse();
            }
            if (i >= MaxOptimisticRetries)
            {
                throw new ConflictException("Job is being modified concurrently; retry.");
            }
            var reloaded = await jobs.GetAsync(job.JobId, ct).ConfigureAwait(false) ?? throw new NotFoundException("Job disappeared.");
            if (reloaded.CurrentAttemptId != attemptId || reloaded.IsTerminal)
            {
                throw new ConflictException("Attempt is no longer current.");
            }
            job = reloaded;
        }
    }

    /// <summary>Writes the attempt result to history and teaches the ML model.</summary>
    private async Task RecordAttemptAsync(JobDTO job, Guid? attemptId, Guid? workerId, AttemptOutcome outcome, double durationMs, string? error,
        DateTimeOffset now, CancellationToken ct)
    {
        if (attemptId is null || workerId is null)
        {
            return;
        }

        // Step 1: history row.
        var attempt = await attempts.GetAsync(attemptId.Value, ct).ConfigureAwait(false);
        if (attempt is not null)
        {
            attempt.FinishedUtc = now;
            attempt.Outcome = outcome;
            attempt.DurationMs = durationMs > 0 ? durationMs : attempt.StartedUtc is { } s ? (now - s).TotalMilliseconds : null;
            attempt.Error = error;
            await attempts.UpdateAsync(attempt, ct).ConfigureAwait(false);
        }

        // Step 2: online learning (this instance; other instances converge through periodic retraining from history).
        var worker = await workers.GetAsync(workerId.Value, ct).ConfigureAwait(false);
        if (worker is not null)
        {
            model.Observe(worker, job.JobType, durationMs, outcome, attempt?.WorkerLoadAtAssign ?? 0, now);
        }
    }

    /// <summary>Loads a job and verifies the reporting worker still owns the attempt.</summary>
    private async Task<JobDTO> LoadOwnedAsync(Guid workerId, Guid jobId, Guid attemptId, CancellationToken ct)
    {
        var job = await jobs.GetAsync(jobId, ct).ConfigureAwait(false) ?? throw new NotFoundException($"Job {jobId} not found.");
        if (job.AssignedWorkerId != workerId || job.CurrentAttemptId != attemptId || !(job.Status is JobStatus.Assigned or JobStatus.Running or JobStatus.CancelRequested))
        {
            tracker.Rejected(workerId.ToString());
            metrics.WorkerReportRejected("stale_attempt");
            throw new ConflictException("This attempt is no longer owned by the worker (job was re-distributed, cancelled or finished).");
        }
        return job;
    }
}
