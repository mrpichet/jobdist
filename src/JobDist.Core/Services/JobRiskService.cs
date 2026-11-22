using JobDist.Contracts;
using JobDist.Core.DAO;
using JobDist.Core.Domain;
using JobDist.Core.Observability;
using Microsoft.Extensions.Options;

namespace JobDist.Core.Services;

/// <summary>
/// Explainable risk scoring (0-100) used by operators and AI agents (MCP tools):
/// <list type="bullet">
/// <item><see cref="AssessJobAsync"/> - will this job fail or be late?</item>
/// <item><see cref="AssessSubmissionAsync"/> - what-if before submitting a job.</item>
/// <item><see cref="AssessWorkerAsync"/> - is this worker unhealthy?</item>
/// </list>
/// Each factor adds points with a human readable explanation so the result can be trusted and acted upon.
/// </summary>
public sealed class JobRiskService(
    IJobDAO jobs,
    IWorkerDAO workers,
    PerformanceLearningService model,
    JobDistMetrics metrics,
    IOptions<Options.JobDistOptions> options,
    TimeProvider time)
{
    /// <summary>
    /// Risk that an existing job fails, is delayed or is lost.
    /// </summary>
    /// <param name="jobId">Job.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Score, level, factors and a recommendation.</returns>
    public async Task<RiskAssessmentDTO> AssessJobAsync(Guid jobId, CancellationToken ct)
    {
        var job = await jobs.GetAsync(jobId, ct).ConfigureAwait(false) ?? throw new NotFoundException($"Job {jobId} not found.");
        if (job.IsTerminal)
        {
            return Finish($"job:{jobId}", [new RiskFactorDTO("finished", 0, $"Job already {job.Status}.")]);
        }

        // Step 1: common factors about capacity / capability of the job type.
        var factors = await CommonFactorsAsync(job.JobType, job.TargetWorkerId, job.StrictTarget, job.TimeoutSeconds, job.Sensitive, ct).ConfigureAwait(false);

        // Step 2: attempts already used.
        if (job.Attempt > 0)
        {
            var used = (double)job.Attempt / Math.Max(1, job.MaxAttempts);
            factors.Add(new RiskFactorDTO("attempts_used", Math.Round(15 * used, 1), $"{job.Attempt} of {job.MaxAttempts} attempts used; last error: {job.LastError ?? "n/a"}."));
        }

        // Step 3: the worker currently holding the job is unstable.
        if (job.AssignedWorkerId is { } wid && await workers.GetAsync(wid, ct).ConfigureAwait(false) is { } holder)
        {
            if (holder.OfflineCount >= 3)
            {
                factors.Add(new RiskFactorDTO("unstable_worker", Math.Min(20, holder.OfflineCount * 3), $"Assigned worker {holder.Name} has been offline {holder.OfflineCount} times."));
            }
            var silence = (time.GetUtcNow() - holder.LastHeartbeatUtc).TotalSeconds;
            if (silence > options.Value.HeartbeatIntervalSeconds * 2)
            {
                factors.Add(new RiskFactorDTO("heartbeat_late", 15, $"No heartbeat from {holder.Name} for {silence:F0}s."));
            }
        }

        // Step 4: long wait in the queue.
        if (job.Status == JobStatus.Pending)
        {
            var waited = (time.GetUtcNow() - job.NextAttemptUtc).TotalMinutes;
            if (waited > 10)
            {
                factors.Add(new RiskFactorDTO("queue_wait", Math.Min(15, waited / 4), $"Ready for {waited:F0} minutes without being scheduled."));
            }
        }
        return Finish($"job:{jobId}", factors);
    }

    /// <summary>
    /// What-if risk of submitting a job with the given parameters (nothing is created).
    /// </summary>
    /// <param name="jobType">Job type.</param>
    /// <param name="targetWorkerId">Optional target worker.</param>
    /// <param name="strictTarget">Run only on the target.</param>
    /// <param name="timeoutSeconds">Timeout (null = default).</param>
    /// <param name="sensitive">Sensitive payload.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<RiskAssessmentDTO> AssessSubmissionAsync(string jobType, Guid? targetWorkerId, bool strictTarget, int? timeoutSeconds, bool sensitive, CancellationToken ct)
    {
        var factors = await CommonFactorsAsync(jobType, targetWorkerId, strictTarget, timeoutSeconds ?? options.Value.DefaultTimeoutSeconds, sensitive, ct).ConfigureAwait(false);
        return Finish($"submission:{jobType}", factors);
    }

    /// <summary>
    /// Health risk of a worker.
    /// </summary>
    /// <param name="workerId">Worker.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<RiskAssessmentDTO> AssessWorkerAsync(Guid workerId, CancellationToken ct)
    {
        var w = await workers.GetAsync(workerId, ct).ConfigureAwait(false) ?? throw new NotFoundException($"Worker {workerId} not found.");
        var factors = new List<RiskFactorDTO>();

        // Step 1: availability.
        if (w.Status is WorkerStatus.Offline or WorkerStatus.Unregistered)
        {
            factors.Add(new RiskFactorDTO("not_available", 50, $"Worker is {w.Status}."));
        }
        if (w.OfflineCount > 0)
        {
            factors.Add(new RiskFactorDTO("network_instability", Math.Min(25, w.OfflineCount * 5), $"Went offline {w.OfflineCount} times since registration."));
        }

        // Step 2: reliability learned by the model, per job type.
        foreach (var s in model.Snapshot().Where(s => s.WorkerId == workerId && s.Samples >= 5))
        {
            var rate = (double)s.Failures / s.Samples;
            if (rate >= 0.2)
            {
                factors.Add(new RiskFactorDTO("failure_rate", Math.Round(Math.Min(25, rate * 40), 1), $"{rate:P0} of {s.Samples} '{s.JobType}' attempts failed."));
            }
        }

        // Step 3: resource pressure.
        if (w.CpuUsagePercent > 90 || w.MemoryUsagePercent > 90)
        {
            factors.Add(new RiskFactorDTO("resource_pressure", 10, $"CPU {w.CpuUsagePercent:F0}%, memory {w.MemoryUsagePercent:F0}%."));
        }
        if (w.MlKemPublicKey is null)
        {
            factors.Add(new RiskFactorDTO("no_pqc", 3, "Worker has no ML-KEM key: sensitive payloads use classical ECDH only."));
        }
        return Finish($"worker:{w.Name}", factors);
    }

    /// <summary>Capacity / capability / history factors shared by job and submission assessments.</summary>
    private async Task<List<RiskFactorDTO>> CommonFactorsAsync(string jobType, Guid? targetWorkerId, bool strictTarget, int timeoutSeconds, bool sensitive, CancellationToken ct)
    {
        var factors = new List<RiskFactorDTO>();
        var all = await workers.ListAsync(false, ct).ConfigureAwait(false);
        var capable = all.Where(w => w.Status == WorkerStatus.Online && w.CapabilityFor(jobType) is not null).ToList();

        // Step 1: capability.
        if (capable.Count == 0)
        {
            factors.Add(new RiskFactorDTO("no_capable_worker", 45, $"No online worker registered job type '{jobType}'. The job will wait."));
        }
        else if (capable.Count == 1)
        {
            factors.Add(new RiskFactorDTO("single_worker", 10, $"Only {capable[0].Name} can run '{jobType}' - no redundancy if it goes offline."));
        }

        // Step 2: strict / preferred target availability.
        if (targetWorkerId is { } tid)
        {
            var target = all.FirstOrDefault(w => w.WorkerId == tid);
            if (target is null || target.Status != WorkerStatus.Online)
            {
                factors.Add(strictTarget
                    ? new RiskFactorDTO("target_offline", 40, "Strict target worker is not online; the job cannot run until it returns.")
                    : new RiskFactorDTO("preferred_offline", 5, "Preferred worker is not online; another worker will be used."));
            }
        }

        // Step 3: backlog vs capacity.
        var pending = (await jobs.PendingByTypeAsync(ct).ConfigureAwait(false)).GetValueOrDefault(jobType);
        var capacity = capable.Sum(w => w.CapabilityFor(jobType)!.MaxConcurrency);
        if (capacity > 0 && pending > capacity)
        {
            var ratio = (double)pending / capacity;
            factors.Add(new RiskFactorDTO("backlog", Math.Round(Math.Min(20, ratio * 2), 1), $"{pending} '{jobType}' jobs queued for {capacity} slots ({ratio:F1}x)."));
        }

        // Step 4: history - failure rate and expected duration vs timeout (from the ML model).
        if (model.FailureRate(jobType) is { } rate && rate > 0.05)
        {
            factors.Add(new RiskFactorDTO("historical_failures", Math.Round(Math.Min(30, rate * 30), 1), $"{rate:P0} of past '{jobType}' attempts failed."));
        }
        if (capable.Count > 0)
        {
            var bestMs = capable.Min(w => model.Predict(w, jobType, 0).ExpectedDurationMs);
            if (bestMs > timeoutSeconds * 1000 * 0.8)
            {
                factors.Add(new RiskFactorDTO("timeout_tight", 20, $"Best expected duration {bestMs / 1000:F1}s is close to / above the {timeoutSeconds}s timeout."));
            }
        }

        // Step 5: confidentiality of sensitive payloads.
        if (sensitive && capable.Count > 0 && capable.All(w => w.EcdhPublicKey is null))
        {
            factors.Add(new RiskFactorDTO("no_encryption_key", 40, "Sensitive job but no capable worker published an encryption key."));
        }
        else if (sensitive && capable.Any(w => w.MlKemPublicKey is null))
        {
            factors.Add(new RiskFactorDTO("classical_only_worker", 3, "Some capable workers lack ML-KEM (no post-quantum protection for this payload)."));
        }
        return factors;
    }

    /// <summary>Sums factors into a 0-100 score with level and recommendation.</summary>
    private RiskAssessmentDTO Finish(string subject, List<RiskFactorDTO> factors)
    {
        var score = Math.Round(Math.Clamp(factors.Sum(f => f.Points), 0, 100), 1);
        var level = score switch { < 25 => "Low", < 50 => "Medium", < 75 => "High", _ => "Critical" };
        var top = factors.OrderByDescending(f => f.Points).FirstOrDefault();
        var recommendation = top?.Factor switch
        {
            "no_capable_worker" or "target_offline" => "Start or register a worker that supports this job type, or remove the strict target.",
            "single_worker" => "Register a second worker for this job type to remove the single point of failure.",
            "backlog" => "Add capacity (more workers or higher MaxConcurrency) or lower the priority of less urgent jobs.",
            "historical_failures" or "failure_rate" => "Inspect recent errors of this job type / worker before submitting more work.",
            "timeout_tight" => "Increase TimeoutSeconds or target faster workers.",
            "unstable_worker" or "network_instability" or "heartbeat_late" => "Check the worker's network; drain it if it keeps dropping.",
            "no_encryption_key" => "Upgrade the workers to the current SDK so they publish encryption keys.",
            null => "No action needed.",
            _ => "Review the listed factors."
        };
        metrics.RiskScore(subject.Split(':')[0], score);
        return new RiskAssessmentDTO(subject, score, level, factors, recommendation);
    }
}
