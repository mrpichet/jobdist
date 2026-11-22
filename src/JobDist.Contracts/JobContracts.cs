using System.ComponentModel.DataAnnotations;

namespace JobDist.Contracts;

// -----------------------------------------------------------------------------------------------------------------
// Job submission API (referenced by any project that sends jobs; see JobDist.Client).
// DataAnnotations are validated by the server ([ApiController]) and again inside the service layer.
// -----------------------------------------------------------------------------------------------------------------

/// <summary>Submit one job.</summary>
/// <param name="JobType">Job type, e.g. <c>image.resize</c>. Only workers that registered this type can run it.</param>
/// <param name="Payload">Job input (normally JSON). Max 1 MB.</param>
/// <param name="Priority">0 (lowest) to 100 (highest). Default 50.</param>
/// <param name="TargetWorkerId">Optional worker that should run the job.</param>
/// <param name="StrictTarget"><c>true</c> = run only on <paramref name="TargetWorkerId"/>; <c>false</c> = prefer it.</param>
/// <param name="MaxAttempts">Max executions including retries (1-20). Null = server default.</param>
/// <param name="TimeoutSeconds">Execution timeout per attempt (1-86400). Null = server default.</param>
/// <param name="RunAtUtc">Optional earliest start time (scheduled job).</param>
/// <param name="IdempotencyKey">Optional key; submitting the same key twice returns the first job (safe client retry).</param>
/// <param name="Sensitive"><c>true</c> = payload encrypted at rest and delivered to the worker in a PQC envelope.</param>
/// <param name="Tags">Optional free tags (max 10).</param>
public sealed record SubmitJobRequest(
    [Required, StringLength(100, MinimumLength = 1), RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9._\-]*$")] string JobType,
    [StringLength(1_048_576)] string? Payload = null,
    [Range(0, 100)] int Priority = 50,
    Guid? TargetWorkerId = null,
    bool StrictTarget = false,
    [Range(1, 20)] int? MaxAttempts = null,
    [Range(1, 86_400)] int? TimeoutSeconds = null,
    DateTimeOffset? RunAtUtc = null,
    [StringLength(100)] string? IdempotencyKey = null,
    bool Sensitive = false,
    [MaxLength(10)] IReadOnlyList<string>? Tags = null);

/// <summary>Submit many jobs in one call (high-throughput ingestion).</summary>
/// <param name="Jobs">1 - 5,000 jobs.</param>
public sealed record SubmitJobBatchRequest([Required, MinLength(1), MaxLength(5_000)] IReadOnlyList<SubmitJobRequest> Jobs);

/// <summary>Result of a submission.</summary>
/// <param name="JobId">New (or existing, when idempotent) job id.</param>
/// <param name="Status">Current status.</param>
/// <param name="Duplicate"><c>true</c> when the idempotency key matched an existing job.</param>
public sealed record SubmitJobResponse(Guid JobId, JobStatus Status, bool Duplicate);

/// <summary>Change the priority of a job that has not finished.</summary>
/// <param name="Priority">0 - 100.</param>
public sealed record ChangePriorityRequest([Range(0, 100)] int Priority);

/// <summary>Cancel a job.</summary>
/// <param name="Reason">Optional reason (stored and sent to the worker).</param>
public sealed record CancelJobRequest([StringLength(500)] string? Reason = null);

/// <summary>Public view of a job.</summary>
public sealed record JobStatusResponse(
    Guid JobId,
    string JobType,
    JobStatus Status,
    int Priority,
    Guid? TargetWorkerId,
    bool StrictTarget,
    Guid? AssignedWorkerId,
    int Attempt,
    int MaxAttempts,
    int TimeoutSeconds,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? RunAtUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    double? LastDurationMs,
    string? Result,
    string? LastError,
    string? CancelReason,
    bool Sensitive,
    IReadOnlyList<string> Tags);

/// <summary>One execution attempt (distribution history).</summary>
public sealed record JobAttemptResponse(
    Guid AttemptId,
    Guid JobId,
    int Attempt,
    Guid WorkerId,
    string? WorkerName,
    DateTimeOffset AssignedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? FinishedUtc,
    double? DurationMs,
    double QueueWaitMs,
    AttemptOutcome Outcome,
    string? Error,
    double SchedulerScore);

/// <summary>Paged list.</summary>
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Total, int Skip, int Take);
