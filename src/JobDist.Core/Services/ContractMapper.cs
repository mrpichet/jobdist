using JobDist.Contracts;
using JobDist.Core.Domain;

namespace JobDist.Core.Services;

/// <summary>Maps domain DTOs to public contracts (internal fields such as token hashes never leave the server).</summary>
public static class ContractMapper
{
    /// <summary>Job → public status view. Sensitive payloads/results are never echoed.</summary>
    public static JobStatusResponse ToResponse(this JobDTO j) => new(
        j.JobId, j.JobType, j.Status, j.Priority, j.TargetWorkerId, j.StrictTarget, j.AssignedWorkerId, j.Attempt, j.MaxAttempts,
        j.TimeoutSeconds, j.CreatedUtc, j.RunAtUtc, j.StartedUtc, j.CompletedUtc, j.LastDurationMs,
        j.Sensitive ? null : j.Result, j.LastError, j.CancelReason, j.Sensitive, j.Tags);

    /// <summary>Attempt → public history entry.</summary>
    public static JobAttemptResponse ToResponse(this JobAttemptDTO a, string? workerName) => new(
        a.AttemptId, a.JobId, a.Attempt, a.WorkerId, workerName, a.AssignedUtc, a.StartedUtc, a.FinishedUtc, a.DurationMs,
        a.QueueWaitMs, a.Outcome, a.Error, a.SchedulerScore);

    /// <summary>Worker → public view.</summary>
    public static WorkerResponse ToResponse(this WorkerDTO w, int activeJobs) => new(
        w.WorkerId, w.Name, w.Status,
        w.Capabilities.Select(c => new WorkerCapabilityRequest(c.JobType, c.MaxConcurrency, c.SpeedFactor)).ToList(),
        new WorkerSpecRequest(w.Spec.CpuCores, w.Spec.MemoryMb, w.Spec.GpuCount, w.Spec.BenchmarkScore, w.Spec.Os),
        w.MaxTotalConcurrency, activeJobs, w.CpuUsagePercent, w.MemoryUsagePercent, w.RegisteredUtc, w.LastHeartbeatUtc,
        w.OfflineCount, w.MlKemPublicKey is not null, w.Version, w.Tags);
}
