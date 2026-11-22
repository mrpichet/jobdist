using System.Text;
using JobDist.Contracts;
using JobDist.Contracts.Security;
using JobDist.Core.DAO;
using JobDist.Core.Domain;
using JobDist.Core.Observability;
using JobDist.Core.Options;
using JobDist.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobDist.Core.Services;

/// <summary>
/// Job submission and management: submit (single / batch, idempotent), query status, change priority, cancel.
/// Cancelling a job held by a worker queues a signed cancel command that the worker receives on its next heartbeat / poll.
/// </summary>
public sealed class JobService(
    IJobDAO jobs,
    IJobAttemptDAO attempts,
    IWorkerDAO workers,
    IWorkerCommandDAO commands,
    PayloadProtectionService protector,
    WorkerSignalService signals,
    JobDistMetrics metrics,
    IOptions<JobDistOptions> options,
    TimeProvider time,
    ILogger<JobService> logger)
{
    private const int MaxOptimisticRetries = 5;

    /// <summary>
    /// Submits a job.
    /// </summary>
    /// <param name="request">Submission.</param>
    /// <param name="submittedBy">Authenticated caller name (scopes idempotency keys per client).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Job id and status; <c>Duplicate</c> = idempotency key matched an earlier job.</returns>
    /// <exception cref="ValidationException">Invalid input.</exception>
    public async Task<SubmitJobResponse> SubmitAsync(SubmitJobRequest request, string submittedBy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Step 1: idempotency - a client retrying after a network error gets the original job back.
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var existing = await jobs.GetByIdempotencyKeyAsync(submittedBy, request.IdempotencyKey, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                return new SubmitJobResponse(existing.JobId, existing.Status, true);
            }
        }

        // Step 2: validate and build the job (business rules beyond the contract annotations).
        var job = await BuildJobAsync(request, submittedBy, ct).ConfigureAwait(false);

        // Step 3: persist; a concurrent duplicate with the same key loses on the unique index → return the winner.
        try
        {
            await jobs.InsertAsync(job, ct).ConfigureAwait(false);
        }
        catch (ConflictException) when (job.IdempotencyKey is not null)
        {
            var winner = await jobs.GetByIdempotencyKeyAsync(submittedBy, job.IdempotencyKey, ct).ConfigureAwait(false);
            if (winner is not null)
            {
                return new SubmitJobResponse(winner.JobId, winner.Status, true);
            }
            throw;
        }

        // Step 4: metrics.
        metrics.JobSubmitted(job.JobType);
        logger.LogDebug("Job {JobId} ({JobType}) submitted by {Client} with priority {Priority}.", job.JobId, job.JobType, submittedBy, job.Priority);
        return new SubmitJobResponse(job.JobId, job.Status, false);
    }

    /// <summary>
    /// Submits many jobs in one database round trip (high-throughput ingestion). Items with an idempotency key that already
    /// exists are returned as duplicates and not inserted again.
    /// </summary>
    /// <param name="request">Batch.</param>
    /// <param name="submittedBy">Caller.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>One response per input item, same order.</returns>
    public async Task<IReadOnlyList<SubmitJobResponse>> SubmitBatchAsync(SubmitJobBatchRequest request, string submittedBy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Jobs is null || request.Jobs.Count is 0 or > 5_000)
        {
            throw new ValidationException("A batch must contain 1 to 5,000 jobs.");
        }

        // Step 1: build every job first so one invalid item rejects the whole batch (all-or-nothing).
        var responses = new SubmitJobResponse[request.Jobs.Count];
        var toInsert = new List<JobDTO>(request.Jobs.Count);
        var seenKeys = new Dictionary<string, Guid>(StringComparer.Ordinal);
        for (var i = 0; i < request.Jobs.Count; i++)
        {
            var item = request.Jobs[i];

            // Step 1a: duplicates inside the batch or against stored jobs.
            if (!string.IsNullOrWhiteSpace(item.IdempotencyKey))
            {
                if (seenKeys.TryGetValue(item.IdempotencyKey, out var sameBatch))
                {
                    responses[i] = new SubmitJobResponse(sameBatch, JobStatus.Pending, true);
                    continue;
                }
                var existing = await jobs.GetByIdempotencyKeyAsync(submittedBy, item.IdempotencyKey, ct).ConfigureAwait(false);
                if (existing is not null)
                {
                    responses[i] = new SubmitJobResponse(existing.JobId, existing.Status, true);
                    continue;
                }
            }
            var job = await BuildJobAsync(item, submittedBy, ct).ConfigureAwait(false);
            if (job.IdempotencyKey is not null)
            {
                seenKeys[job.IdempotencyKey] = job.JobId;
            }
            toInsert.Add(job);
            responses[i] = new SubmitJobResponse(job.JobId, job.Status, false);
        }

        // Step 2: bulk insert and metrics per type.
        if (toInsert.Count > 0)
        {
            await jobs.InsertBatchAsync(toInsert, ct).ConfigureAwait(false);
            foreach (var g in toInsert.GroupBy(j => j.JobType))
            {
                metrics.JobSubmitted(g.Key, g.Count());
            }
        }
        return responses;
    }

    /// <summary>Gets the status of a job.</summary>
    /// <exception cref="NotFoundException">Unknown job.</exception>
    public async Task<JobStatusResponse> GetAsync(Guid jobId, CancellationToken ct) =>
        (await LoadAsync(jobId, ct).ConfigureAwait(false)).ToResponse();

    /// <summary>Lists jobs.</summary>
    /// <param name="query">Filter; <c>Take</c> is clamped to 1-1000.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Page of jobs.</returns>
    public async Task<PagedResponse<JobStatusResponse>> QueryAsync(JobQueryDTO query, CancellationToken ct)
    {
        var q = query with { Skip = Math.Max(0, query.Skip), Take = Math.Clamp(query.Take, 1, 1000) };
        var (items, total) = await jobs.QueryAsync(q, ct).ConfigureAwait(false);
        return new PagedResponse<JobStatusResponse>(items.Select(j => j.ToResponse()).ToList(), total, q.Skip, q.Take);
    }

    /// <summary>Distribution history of a job (attempts, workers, timings).</summary>
    public async Task<IReadOnlyList<JobAttemptResponse>> GetAttemptsAsync(Guid jobId, CancellationToken ct)
    {
        await LoadAsync(jobId, ct).ConfigureAwait(false);
        var list = await attempts.ListByJobAsync(jobId, ct).ConfigureAwait(false);
        var names = (await workers.ListAsync(true, ct).ConfigureAwait(false)).ToDictionary(w => w.WorkerId, w => w.Name);
        return list.Select(a => a.ToResponse(names.GetValueOrDefault(a.WorkerId))).ToList();
    }

    /// <summary>
    /// Changes the priority of an unfinished job. Takes effect on the next scheduling cycle.
    /// </summary>
    /// <param name="jobId">Job.</param>
    /// <param name="priority">0-100.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Updated job.</returns>
    /// <exception cref="ConflictException">Job already finished.</exception>
    public async Task<JobStatusResponse> ChangePriorityAsync(Guid jobId, int priority, CancellationToken ct)
    {
        if (priority is < 0 or > 100)
        {
            throw new ValidationException("Priority must be between 0 and 100.");
        }

        // Optimistic concurrency loop: re-read and retry if another instance changed the job meanwhile.
        for (var attempt = 0; attempt < MaxOptimisticRetries; attempt++)
        {
            var job = await LoadAsync(jobId, ct).ConfigureAwait(false);
            if (job.IsTerminal)
            {
                throw new ConflictException($"Job is already {job.Status}.");
            }
            job.Priority = priority;
            if (await jobs.TryUpdateAsync(job, ct).ConfigureAwait(false))
            {
                job.Version++;
                return job.ToResponse();
            }
        }
        throw new ConflictException("Job is being modified concurrently; try again.");
    }

    /// <summary>
    /// Cancels a job. Pending → Cancelled immediately. Held by a worker → CancelRequested and a signed cancel command is queued
    /// for that worker; the job becomes Cancelled when the worker acknowledges (or is lost).
    /// </summary>
    /// <param name="jobId">Job.</param>
    /// <param name="reason">Optional reason.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Updated job.</returns>
    /// <exception cref="ConflictException">Job already finished.</exception>
    public async Task<JobStatusResponse> CancelAsync(Guid jobId, string? reason, CancellationToken ct)
    {
        reason = InputGuard.CleanText(reason, 500);
        for (var attempt = 0; attempt < MaxOptimisticRetries; attempt++)
        {
            // Step 1: load and check state.
            var job = await LoadAsync(jobId, ct).ConfigureAwait(false);
            if (job.Status == JobStatus.CancelRequested || job.Status == JobStatus.Cancelled)
            {
                return job.ToResponse();
            }
            if (job.IsTerminal)
            {
                throw new ConflictException($"Job is already {job.Status}.");
            }

            // Step 2: not on a worker → cancel right away.
            var now = time.GetUtcNow();
            var held = job.Status is JobStatus.Assigned or JobStatus.Running && job.AssignedWorkerId is not null;
            job.CancelReason = reason ?? "Cancelled by request";
            if (!held)
            {
                job.Status = JobStatus.Cancelled;
                job.CompletedUtc = now;
            }
            else
            {
                job.Status = JobStatus.CancelRequested;
            }
            if (!await jobs.TryUpdateAsync(job, ct).ConfigureAwait(false))
            {
                continue;
            }
            job.Version++;

            // Step 3: on a worker → queue a command and wake the worker's long-poll.
            if (held)
            {
                await commands.InsertAsync(new WorkerCommandDTO
                {
                    CommandId = Guid.CreateVersion7(),
                    WorkerId = job.AssignedWorkerId!.Value,
                    Kind = WorkerCommandKind.CancelJob,
                    JobId = job.JobId,
                    Reason = job.CancelReason,
                    IssuedUtc = now
                }, ct).ConfigureAwait(false);
                signals.Notify(job.AssignedWorkerId.Value);
            }
            else
            {
                metrics.JobCancelled(job.JobType);
            }
            logger.LogInformation("Job {JobId} cancel requested ({State}).", job.JobId, job.Status);
            return job.ToResponse();
        }
        throw new ConflictException("Job is being modified concurrently; try again.");
    }

    /// <summary>Validates a submission and creates the domain object.</summary>
    private async Task<JobDTO> BuildJobAsync(SubmitJobRequest r, string submittedBy, CancellationToken ct)
    {
        var o = options.Value;
        var now = time.GetUtcNow();

        // Step 1: field rules (repeated here because services are also called from MCP tools, not only from controllers).
        if (!InputGuard.IsValidJobType(r.JobType))
        {
            throw new ValidationException("JobType must be 1-100 characters: letters, digits, '.', '_' or '-'.");
        }
        var payloadBytes = r.Payload is null ? 0 : Encoding.UTF8.GetByteCount(r.Payload);
        if (payloadBytes > o.MaxPayloadBytes)
        {
            throw new ValidationException($"Payload exceeds {o.MaxPayloadBytes} bytes.");
        }
        if (r.Priority is < 0 or > 100)
        {
            throw new ValidationException("Priority must be between 0 and 100.");
        }
        if (r.MaxAttempts is < 1 or > 20 || r.TimeoutSeconds is < 1 or > 86_400)
        {
            throw new ValidationException("MaxAttempts must be 1-20 and TimeoutSeconds 1-86400.");
        }
        if (r.RunAtUtc is { } runAt && runAt > now.AddDays(365))
        {
            throw new ValidationException("RunAtUtc cannot be more than one year ahead.");
        }
        if (r.Tags is { Count: > 10 } || r.Tags?.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 50) == true)
        {
            throw new ValidationException("At most 10 tags of 1-50 characters.");
        }

        // Step 2: a strict target must exist and be able to run the type (fail fast instead of a job that never runs).
        if (r.StrictTarget)
        {
            if (r.TargetWorkerId is null)
            {
                throw new ValidationException("StrictTarget requires TargetWorkerId.");
            }
            var target = await workers.GetAsync(r.TargetWorkerId.Value, ct).ConfigureAwait(false);
            if (target is null || target.Status == WorkerStatus.Unregistered)
            {
                throw new ValidationException("Target worker does not exist.");
            }
            if (target.CapabilityFor(r.JobType) is null)
            {
                throw new ValidationException($"Target worker cannot run job type '{r.JobType}'.");
            }
        }

        // Step 3: build; sensitive payloads are encrypted at rest immediately.
        var job = new JobDTO
        {
            JobId = Guid.CreateVersion7(),
            JobType = r.JobType,
            PayloadSha256 = CanonicalDigest.PayloadHash(r.Payload),
            PayloadSize = payloadBytes,
            Status = JobStatus.Pending,
            Priority = r.Priority,
            TargetWorkerId = r.TargetWorkerId,
            StrictTarget = r.StrictTarget,
            MaxAttempts = r.MaxAttempts ?? o.DefaultMaxAttempts,
            TimeoutSeconds = r.TimeoutSeconds ?? o.DefaultTimeoutSeconds,
            CreatedUtc = now,
            RunAtUtc = r.RunAtUtc,
            NextAttemptUtc = r.RunAtUtc is { } at && at > now ? at : now,
            IdempotencyKey = string.IsNullOrWhiteSpace(r.IdempotencyKey) ? null : r.IdempotencyKey.Trim(),
            SubmittedBy = submittedBy,
            Sensitive = r.Sensitive,
            Tags = r.Tags?.Select(t => t.Trim()).ToList() ?? []
        };
        if (r.Sensitive && r.Payload is not null)
        {
            job.ProtectedPayload = protector.Protect(job.JobId, r.Payload);
        }
        else
        {
            job.Payload = r.Payload;
        }
        return job;
    }

    private async Task<JobDTO> LoadAsync(Guid jobId, CancellationToken ct) =>
        await jobs.GetAsync(jobId, ct).ConfigureAwait(false) ?? throw new NotFoundException($"Job {jobId} not found.");
}
