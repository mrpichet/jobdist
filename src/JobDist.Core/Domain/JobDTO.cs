using JobDist.Contracts;

namespace JobDist.Core.Domain;

/// <summary>A unit of work submitted to the cluster.</summary>
public sealed class JobDTO
{
    public Guid JobId { get; set; }
    public string JobType { get; set; } = string.Empty;
    /// <summary>Plain payload (null when <see cref="Sensitive"/>; then <see cref="ProtectedPayload"/> holds it).</summary>
    public string? Payload { get; set; }
    /// <summary>AES-256-GCM protected payload (Base64) for sensitive jobs, encrypted at rest.</summary>
    public string? ProtectedPayload { get; set; }
    /// <summary>Hex SHA-256 of the plaintext payload.</summary>
    public string PayloadSha256 { get; set; } = string.Empty;
    public int PayloadSize { get; set; }
    public JobStatus Status { get; set; }
    /// <summary>0-100, higher runs first.</summary>
    public int Priority { get; set; }
    public Guid? TargetWorkerId { get; set; }
    public bool StrictTarget { get; set; }
    public Guid? AssignedWorkerId { get; set; }
    public Guid? CurrentAttemptId { get; set; }
    /// <summary>Number of attempts started so far.</summary>
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; }
    public int TimeoutSeconds { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? RunAtUtc { get; set; }
    /// <summary>Earliest time the job may be scheduled (retry back-off).</summary>
    public DateTimeOffset NextAttemptUtc { get; set; }
    public DateTimeOffset? AssignedUtc { get; set; }
    /// <summary>When the worker's claim on the job expires if it never reports back.</summary>
    public DateTimeOffset? LeaseExpiresUtc { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public double? LastDurationMs { get; set; }
    public string? Result { get; set; }
    public string? LastError { get; set; }
    public string? CancelReason { get; set; }
    /// <summary>Worker that ran the last failed attempt (avoid it on retry when possible).</summary>
    public Guid? LastFailedWorkerId { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? SubmittedBy { get; set; }
    public bool Sensitive { get; set; }
    public List<string> Tags { get; set; } = [];
    /// <summary>Optimistic concurrency token.</summary>
    public long Version { get; set; }

    /// <summary><c>true</c> when the job reached a final state.</summary>
    public bool IsTerminal => Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;

    /// <summary><c>true</c> when a worker currently holds the job.</summary>
    public bool IsHeldByWorker => Status is JobStatus.Assigned or JobStatus.Running or JobStatus.CancelRequested && AssignedWorkerId is not null;

    /// <summary>Shallow copy (used by in-memory stores so callers cannot mutate stored state).</summary>
    public JobDTO Clone()
    {
        var copy = (JobDTO)MemberwiseClone();
        copy.Tags = [.. Tags];
        return copy;
    }
}

/// <summary>One execution attempt of a job (distribution history, input to the ML model).</summary>
public sealed class JobAttemptDTO
{
    public Guid AttemptId { get; set; }
    public Guid JobId { get; set; }
    public int Attempt { get; set; }
    public Guid WorkerId { get; set; }
    public string JobType { get; set; } = string.Empty;
    public int Priority { get; set; }
    public int PayloadSize { get; set; }
    public DateTimeOffset AssignedUtc { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public double? DurationMs { get; set; }
    /// <summary>Time between job becoming ready and assignment.</summary>
    public double QueueWaitMs { get; set; }
    public AttemptOutcome Outcome { get; set; }
    public string? Error { get; set; }
    /// <summary>Score the scheduler gave the chosen worker.</summary>
    public double SchedulerScore { get; set; }
    /// <summary>Worker load ratio (0-1) at assignment time (feature for the ML model).</summary>
    public double WorkerLoadAtAssign { get; set; }

    public JobAttemptDTO Clone() => (JobAttemptDTO)MemberwiseClone();
}

/// <summary>Filter for job queries.</summary>
public sealed record JobQueryDTO(JobStatus? Status = null, string? JobType = null, Guid? WorkerId = null, int Skip = 0, int Take = 100);

/// <summary>Assignment decided by the scheduler, persisted atomically.</summary>
/// <param name="JobId">Job.</param>
/// <param name="ExpectedVersion">Version read by the scheduler (optimistic concurrency).</param>
/// <param name="WorkerId">Chosen worker.</param>
/// <param name="AttemptId">New attempt id.</param>
/// <param name="Score">Scheduler score.</param>
/// <param name="WorkerLoad">Load ratio of the worker when chosen.</param>
public sealed record AssignmentDTO(Guid JobId, long ExpectedVersion, Guid WorkerId, Guid AttemptId, double Score, double WorkerLoad);
