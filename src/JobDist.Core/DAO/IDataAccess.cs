using JobDist.Contracts;
using JobDist.Core.Domain;

namespace JobDist.Core.DAO;

// Repository interfaces (suffix DAO). Implementations (suffix ADO) live in JobDist.Infrastructure:
// SQL Server and PostgreSQL through Dapper, plus an in-memory variant for development and tests.
// Every method is async, cancellable and safe to call from several server instances at the same time.

/// <summary>Jobs and their state transitions.</summary>
public interface IJobDAO
{
    /// <summary>Inserts a new job.</summary>
    Task InsertAsync(JobDTO job, CancellationToken ct = default);

    /// <summary>Inserts many jobs in one round trip (bulk ingestion).</summary>
    Task InsertBatchAsync(IReadOnlyList<JobDTO> jobs, CancellationToken ct = default);

    /// <summary>Gets a job or null.</summary>
    Task<JobDTO?> GetAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Finds a job by submitter + idempotency key, or null.</summary>
    Task<JobDTO?> GetByIdempotencyKeyAsync(string submittedBy, string idempotencyKey, CancellationToken ct = default);

    /// <summary>Lists jobs with filtering and paging.</summary>
    /// <returns>Page of jobs (newest first) and the total count.</returns>
    Task<(IReadOnlyList<JobDTO> Items, int Total)> QueryAsync(JobQueryDTO query, CancellationToken ct = default);

    /// <summary>
    /// Pending jobs whose run time / back-off elapsed, ordered by effective priority
    /// (priority + min(100, waited minutes × <paramref name="agingPointsPerMinute"/>)) then oldest first.
    /// </summary>
    Task<IReadOnlyList<JobDTO>> GetReadyPendingAsync(DateTimeOffset now, int take, double agingPointsPerMinute, CancellationToken ct = default);

    /// <summary>
    /// Atomically applies scheduler decisions: sets each job to Assigned (only if still Pending with the expected version)
    /// and inserts the attempt rows.
    /// </summary>
    /// <returns>The assignments that were applied (others lost a race and are skipped).</returns>
    Task<IReadOnlyList<AssignmentDTO>> ApplyAssignmentsAsync(IReadOnlyList<AssignmentDTO> assignments, DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken ct = default);

    /// <summary>Takes jobs assigned to a worker that were not delivered yet and marks them delivered.</summary>
    Task<IReadOnlyList<JobDTO>> TakeUndeliveredAsync(Guid workerId, int max, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Updates a job if its version still matches (optimistic concurrency). Increments the version.</summary>
    /// <returns><c>true</c> when updated.</returns>
    Task<bool> TryUpdateAsync(JobDTO job, CancellationToken ct = default);

    /// <summary>Jobs currently held (Assigned / Running / CancelRequested) by a worker.</summary>
    Task<IReadOnlyList<JobDTO>> GetHeldByWorkerAsync(Guid workerId, CancellationToken ct = default);

    /// <summary>Held jobs whose lease expired.</summary>
    Task<IReadOnlyList<JobDTO>> GetExpiredLeasesAsync(DateTimeOffset now, int take, CancellationToken ct = default);

    /// <summary>Active (Assigned / Running / CancelRequested) job counts per worker and type.</summary>
    Task<IReadOnlyList<WorkerLoadDTO>> GetActiveLoadAsync(CancellationToken ct = default);

    /// <summary>Job counts per status.</summary>
    Task<IReadOnlyDictionary<JobStatus, int>> CountByStatusAsync(CancellationToken ct = default);

    /// <summary>Pending job counts per type.</summary>
    Task<IReadOnlyDictionary<string, int>> PendingByTypeAsync(CancellationToken ct = default);

    /// <summary>Submission timestamps in a window (for spike detection), capped.</summary>
    Task<IReadOnlyList<DateTimeOffset>> GetSubmissionTimesAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, int max, CancellationToken ct = default);

    /// <summary>Deletes terminal jobs (and their attempts) completed before a cut-off.</summary>
    /// <returns>Rows deleted.</returns>
    Task<int> PurgeAsync(DateTimeOffset completedBefore, int batchSize, CancellationToken ct = default);
}

/// <summary>Execution attempts (distribution history and timing).</summary>
public interface IJobAttemptDAO
{
    /// <summary>Gets an attempt or null.</summary>
    Task<JobAttemptDTO?> GetAsync(Guid attemptId, CancellationToken ct = default);

    /// <summary>Updates start / finish data of an attempt.</summary>
    Task UpdateAsync(JobAttemptDTO attempt, CancellationToken ct = default);

    /// <summary>Attempts of one job, oldest first.</summary>
    Task<IReadOnlyList<JobAttemptDTO>> ListByJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Attempts finished since a time (training data / anomaly window), newest first, capped.</summary>
    Task<IReadOnlyList<JobAttemptDTO>> GetFinishedSinceAsync(DateTimeOffset sinceUtc, int max, CancellationToken ct = default);

    /// <summary>Aggregated statistics per worker and job type since a time.</summary>
    Task<IReadOnlyList<PerformanceStatDTO>> GetStatsAsync(DateTimeOffset sinceUtc, CancellationToken ct = default);
}

/// <summary>Registered workers.</summary>
public interface IWorkerDAO
{
    /// <summary>Inserts or replaces a worker (matched by id).</summary>
    Task UpsertAsync(WorkerDTO worker, CancellationToken ct = default);

    /// <summary>Gets a worker or null.</summary>
    Task<WorkerDTO?> GetAsync(Guid workerId, CancellationToken ct = default);

    /// <summary>Gets a worker by unique name or null.</summary>
    Task<WorkerDTO?> GetByNameAsync(string name, CancellationToken ct = default);

    /// <summary>Lists workers.</summary>
    /// <param name="includeUnregistered">Include unregistered workers.</param>
    Task<IReadOnlyList<WorkerDTO>> ListAsync(bool includeUnregistered, CancellationToken ct = default);

    /// <summary>Records a heartbeat (also flips Offline → Online).</summary>
    /// <returns>The status after the update, or null when the worker does not exist / is unregistered.</returns>
    Task<WorkerStatus?> RecordHeartbeatAsync(Guid workerId, DateTimeOffset now, double cpu, double memory, CancellationToken ct = default);

    /// <summary>Changes the status.</summary>
    /// <param name="incrementOfflineCount">Increment <c>OfflineCount</c> (used when the worker is lost).</param>
    /// <param name="onlyIfStatus">Only change when the current status equals this value (race-free transitions).</param>
    /// <returns><c>true</c> when changed.</returns>
    Task<bool> SetStatusAsync(Guid workerId, WorkerStatus status, bool incrementOfflineCount, WorkerStatus? onlyIfStatus, CancellationToken ct = default);

    /// <summary>Online / draining workers whose last heartbeat is older than the cut-off.</summary>
    Task<IReadOnlyList<WorkerDTO>> GetStaleAsync(DateTimeOffset heartbeatBefore, CancellationToken ct = default);
}

/// <summary>Commands queued for workers (cancel / drain).</summary>
public interface IWorkerCommandDAO
{
    /// <summary>Queues a command.</summary>
    Task InsertAsync(WorkerCommandDTO command, CancellationToken ct = default);

    /// <summary>
    /// Returns unacknowledged commands for a worker that were never delivered or were delivered before
    /// <paramref name="redeliverBefore"/>, and stamps them as delivered now (at-least-once delivery).
    /// </summary>
    Task<IReadOnlyList<WorkerCommandDTO>> TakePendingAsync(Guid workerId, DateTimeOffset now, DateTimeOffset redeliverBefore, CancellationToken ct = default);

    /// <summary>Marks every command about a job as acknowledged (job reached a final state).</summary>
    Task AcknowledgeJobAsync(Guid jobId, DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>Leases for leader election across server instances (HA, no single point of failure).</summary>
public interface ILeaderLeaseDAO
{
    /// <summary>
    /// Acquires or renews a lease: succeeds when the lease is free, expired, or already held by <paramref name="holderId"/>.
    /// </summary>
    /// <returns><c>true</c> when the caller holds the lease until <paramref name="expiresUtc"/>.</returns>
    Task<bool> TryAcquireAsync(string name, string holderId, DateTimeOffset now, DateTimeOffset expiresUtc, CancellationToken ct = default);

    /// <summary>Current lease or null.</summary>
    Task<LeaderLeaseDTO?> GetAsync(string name, CancellationToken ct = default);

    /// <summary>Releases the lease if held by the caller (fast fail-over on graceful shutdown).</summary>
    Task ReleaseAsync(string name, string holderId, CancellationToken ct = default);
}
