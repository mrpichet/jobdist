using JobDist.Contracts;
using JobDist.Core.DAO;
using JobDist.Core.Domain;
using JobDist.Core.Services;

namespace JobDist.Infrastructure.Data.InMemory;

/// <summary>
/// Shared state of the in-memory provider (development, demos and tests). A single lock keeps every operation atomic,
/// mirroring the conditional updates of the SQL implementations. Objects are cloned in and out so callers cannot mutate
/// stored state by accident. Not HA: use SQL Server or PostgreSQL for multi-instance deployments.
/// </summary>
public sealed class InMemoryStore
{
    internal readonly Lock Gate = new();
    internal readonly Dictionary<Guid, JobDTO> Jobs = [];
    internal readonly Dictionary<Guid, JobAttemptDTO> Attempts = [];
    internal readonly Dictionary<Guid, WorkerDTO> Workers = [];
    internal readonly Dictionary<Guid, WorkerCommandDTO> Commands = [];
    internal readonly Dictionary<Guid, DateTimeOffset?> CommandAcks = [];
    internal readonly Dictionary<string, LeaderLeaseDTO> Leases = [];
}

/// <summary>In-memory <see cref="IJobDAO"/>.</summary>
public sealed class InMemoryJobADO(InMemoryStore store) : IJobDAO
{
    private static bool IsHeld(JobDTO j) => j.AssignedWorkerId is not null && j.Status is JobStatus.Assigned or JobStatus.Running or JobStatus.CancelRequested;

    /// <inheritdoc />
    public Task InsertAsync(JobDTO job, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            InsertUnsafe(job);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task InsertBatchAsync(IReadOnlyList<JobDTO> jobs, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            // All-or-nothing like the SQL transaction: validate first, then insert.
            foreach (var j in jobs)
            {
                CheckIdempotency(j);
            }
            foreach (var j in jobs)
            {
                InsertUnsafe(j);
            }
        }
        return Task.CompletedTask;
    }

    private void InsertUnsafe(JobDTO job)
    {
        CheckIdempotency(job);
        store.Jobs[job.JobId] = job.Clone();
    }

    private void CheckIdempotency(JobDTO job)
    {
        if (job.IdempotencyKey is not null && store.Jobs.Values.Any(x => x.SubmittedBy == job.SubmittedBy && x.IdempotencyKey == job.IdempotencyKey))
        {
            throw new ConflictException("A job with this idempotency key already exists.");
        }
    }

    /// <inheritdoc />
    public Task<JobDTO?> GetAsync(Guid jobId, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult(store.Jobs.TryGetValue(jobId, out var j) ? j.Clone() : null);
        }
    }

    /// <inheritdoc />
    public Task<JobDTO?> GetByIdempotencyKeyAsync(string submittedBy, string idempotencyKey, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult(store.Jobs.Values.FirstOrDefault(x => x.SubmittedBy == submittedBy && x.IdempotencyKey == idempotencyKey)?.Clone());
        }
    }

    /// <inheritdoc />
    public Task<(IReadOnlyList<JobDTO> Items, int Total)> QueryAsync(JobQueryDTO query, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            var q = store.Jobs.Values.Where(j => (query.Status is null || j.Status == query.Status)
                                               && (query.JobType is null || j.JobType == query.JobType)
                                               && (query.WorkerId is null || j.AssignedWorkerId == query.WorkerId)).ToList();
            IReadOnlyList<JobDTO> page = q.OrderByDescending(j => j.CreatedUtc).Skip(query.Skip).Take(query.Take).Select(j => j.Clone()).ToList();
            return Task.FromResult((page, q.Count));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JobDTO>> GetReadyPendingAsync(DateTimeOffset now, int take, double agingPointsPerMinute, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            IReadOnlyList<JobDTO> list = store.Jobs.Values
                .Where(j => j.Status == JobStatus.Pending && j.NextAttemptUtc <= now)
                .OrderByDescending(j => j.Priority + Math.Min(100, (now - j.NextAttemptUtc).TotalMinutes * agingPointsPerMinute))
                .ThenBy(j => j.NextAttemptUtc)
                .Take(take).Select(j => j.Clone()).ToList();
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AssignmentDTO>> ApplyAssignmentsAsync(IReadOnlyList<AssignmentDTO> assignments, DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken ct = default)
    {
        var applied = new List<AssignmentDTO>();
        lock (store.Gate)
        {
            foreach (var a in assignments)
            {
                // Same condition as SQL: still Pending with the version the scheduler read.
                if (!store.Jobs.TryGetValue(a.JobId, out var j) || j.Status != JobStatus.Pending || j.Version != a.ExpectedVersion)
                {
                    continue;
                }
                j.Status = JobStatus.Assigned;
                j.AssignedWorkerId = a.WorkerId;
                j.CurrentAttemptId = a.AttemptId;
                j.Attempt++;
                j.AssignedUtc = now;
                j.DeliveredUtc = null;
                j.LeaseExpiresUtc = leaseUntil;
                j.Version++;
                var readySince = j.NextAttemptUtc > j.CreatedUtc ? j.NextAttemptUtc : j.CreatedUtc;
                store.Attempts[a.AttemptId] = new JobAttemptDTO
                {
                    AttemptId = a.AttemptId, JobId = j.JobId, Attempt = j.Attempt, WorkerId = a.WorkerId, JobType = j.JobType, Priority = j.Priority,
                    PayloadSize = j.PayloadSize, AssignedUtc = now, QueueWaitMs = Math.Max(0, (now - readySince).TotalMilliseconds),
                    Outcome = AttemptOutcome.InProgress, SchedulerScore = a.Score, WorkerLoadAtAssign = a.WorkerLoad
                };
                applied.Add(a);
            }
        }
        return Task.FromResult<IReadOnlyList<AssignmentDTO>>(applied);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JobDTO>> TakeUndeliveredAsync(Guid workerId, int max, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            var list = store.Jobs.Values
                .Where(j => j.AssignedWorkerId == workerId && j.Status is JobStatus.Assigned or JobStatus.CancelRequested && j.DeliveredUtc is null)
                .OrderByDescending(j => j.Priority).Take(max).ToList();
            foreach (var j in list)
            {
                j.DeliveredUtc = now;
            }
            return Task.FromResult<IReadOnlyList<JobDTO>>(list.Select(j => j.Clone()).ToList());
        }
    }

    /// <inheritdoc />
    public Task<bool> TryUpdateAsync(JobDTO job, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            if (!store.Jobs.TryGetValue(job.JobId, out var current) || current.Version != job.Version)
            {
                return Task.FromResult(false);
            }
            var copy = job.Clone();
            copy.Version = current.Version + 1;
            store.Jobs[job.JobId] = copy;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JobDTO>> GetHeldByWorkerAsync(Guid workerId, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult<IReadOnlyList<JobDTO>>(store.Jobs.Values.Where(j => j.AssignedWorkerId == workerId && IsHeld(j)).Select(j => j.Clone()).ToList());
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JobDTO>> GetExpiredLeasesAsync(DateTimeOffset now, int take, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult<IReadOnlyList<JobDTO>>(store.Jobs.Values.Where(j => IsHeld(j) && j.LeaseExpiresUtc < now)
                .OrderBy(j => j.LeaseExpiresUtc).Take(take).Select(j => j.Clone()).ToList());
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WorkerLoadDTO>> GetActiveLoadAsync(CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult<IReadOnlyList<WorkerLoadDTO>>(store.Jobs.Values.Where(IsHeld)
                .GroupBy(j => (j.AssignedWorkerId!.Value, j.JobType))
                .Select(g => new WorkerLoadDTO(g.Key.Value, g.Key.JobType, g.Count())).ToList());
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<JobStatus, int>> CountByStatusAsync(CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult<IReadOnlyDictionary<JobStatus, int>>(store.Jobs.Values.GroupBy(j => j.Status).ToDictionary(g => g.Key, g => g.Count()));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, int>> PendingByTypeAsync(CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult<IReadOnlyDictionary<string, int>>(store.Jobs.Values.Where(j => j.Status == JobStatus.Pending)
                .GroupBy(j => j.JobType, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DateTimeOffset>> GetSubmissionTimesAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, int max, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult<IReadOnlyList<DateTimeOffset>>(store.Jobs.Values.Select(j => j.CreatedUtc).Where(t => t >= fromUtc && t < toUtc)
                .OrderByDescending(t => t).Take(max).ToList());
        }
    }

    /// <inheritdoc />
    public Task<int> PurgeAsync(DateTimeOffset completedBefore, int batchSize, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            var victims = store.Jobs.Values.Where(j => j.IsTerminal && j.CompletedUtc < completedBefore).Take(batchSize).Select(j => j.JobId).ToList();
            foreach (var id in victims)
            {
                store.Jobs.Remove(id);
                foreach (var a in store.Attempts.Values.Where(a => a.JobId == id).Select(a => a.AttemptId).ToList())
                {
                    store.Attempts.Remove(a);
                }
            }
            return Task.FromResult(victims.Count);
        }
    }
}

/// <summary>In-memory <see cref="IJobAttemptDAO"/>.</summary>
public sealed class InMemoryJobAttemptADO(InMemoryStore store) : IJobAttemptDAO
{
    /// <inheritdoc />
    public Task<JobAttemptDTO?> GetAsync(Guid attemptId, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult(store.Attempts.TryGetValue(attemptId, out var a) ? a.Clone() : null);
        }
    }

    /// <inheritdoc />
    public Task UpdateAsync(JobAttemptDTO attempt, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            if (store.Attempts.ContainsKey(attempt.AttemptId))
            {
                store.Attempts[attempt.AttemptId] = attempt.Clone();
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>Adds an attempt directly (test seeding).</summary>
    public void Seed(JobAttemptDTO attempt)
    {
        lock (store.Gate)
        {
            store.Attempts[attempt.AttemptId] = attempt.Clone();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JobAttemptDTO>> ListByJobAsync(Guid jobId, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult<IReadOnlyList<JobAttemptDTO>>(store.Attempts.Values.Where(a => a.JobId == jobId).OrderBy(a => a.Attempt).ThenBy(a => a.AssignedUtc).Select(a => a.Clone()).ToList());
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JobAttemptDTO>> GetFinishedSinceAsync(DateTimeOffset sinceUtc, int max, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult<IReadOnlyList<JobAttemptDTO>>(store.Attempts.Values.Where(a => a.FinishedUtc >= sinceUtc)
                .OrderByDescending(a => a.FinishedUtc).Take(max).Select(a => a.Clone()).ToList());
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PerformanceStatDTO>> GetStatsAsync(DateTimeOffset sinceUtc, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            var stats = store.Attempts.Values
                .Where(a => a.FinishedUtc >= sinceUtc && a.Outcome is AttemptOutcome.Succeeded or AttemptOutcome.Failed or AttemptOutcome.TimedOut or AttemptOutcome.WorkerLost)
                .GroupBy(a => (a.WorkerId, a.JobType))
                .Select(g =>
                {
                    var ok = g.Where(a => a.Outcome == AttemptOutcome.Succeeded && a.DurationMs is not null).Select(a => a.DurationMs!.Value).ToArray();
                    var (mean, std) = ScoringMath.MeanStdDev(ok, false);
                    return new PerformanceStatDTO(g.Key.WorkerId, g.Key.JobType, g.Count(), g.Count(a => a.Outcome == AttemptOutcome.Succeeded),
                        g.Count(a => a.Outcome != AttemptOutcome.Succeeded), mean, std, g.Max(a => a.FinishedUtc!.Value));
                }).ToList();
            return Task.FromResult<IReadOnlyList<PerformanceStatDTO>>(stats);
        }
    }
}

/// <summary>In-memory <see cref="IWorkerDAO"/>.</summary>
public sealed class InMemoryWorkerADO(InMemoryStore store) : IWorkerDAO
{
    /// <inheritdoc />
    public Task UpsertAsync(WorkerDTO worker, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            if (store.Workers.Values.Any(w => w.Name == worker.Name && w.WorkerId != worker.WorkerId))
            {
                throw new ConflictException("A worker with this name already exists.");
            }
            store.Workers[worker.WorkerId] = worker.Clone();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<WorkerDTO?> GetAsync(Guid workerId, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult(store.Workers.TryGetValue(workerId, out var w) ? w.Clone() : null);
        }
    }

    /// <inheritdoc />
    public Task<WorkerDTO?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult(store.Workers.Values.FirstOrDefault(w => w.Name == name)?.Clone());
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WorkerDTO>> ListAsync(bool includeUnregistered, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult<IReadOnlyList<WorkerDTO>>(store.Workers.Values.Where(w => includeUnregistered || w.Status != WorkerStatus.Unregistered)
                .OrderBy(w => w.Name, StringComparer.Ordinal).Select(w => w.Clone()).ToList());
        }
    }

    /// <inheritdoc />
    public Task<WorkerStatus?> RecordHeartbeatAsync(Guid workerId, DateTimeOffset now, double cpu, double memory, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            if (!store.Workers.TryGetValue(workerId, out var w) || w.Status == WorkerStatus.Unregistered)
            {
                return Task.FromResult<WorkerStatus?>(null);
            }
            w.LastHeartbeatUtc = now;
            w.CpuUsagePercent = cpu;
            w.MemoryUsagePercent = memory;
            if (w.Status == WorkerStatus.Offline)
            {
                w.Status = WorkerStatus.Online;
            }
            return Task.FromResult<WorkerStatus?>(w.Status);
        }
    }

    /// <inheritdoc />
    public Task<bool> SetStatusAsync(Guid workerId, WorkerStatus status, bool incrementOfflineCount, WorkerStatus? onlyIfStatus, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            if (!store.Workers.TryGetValue(workerId, out var w) || (onlyIfStatus is not null && w.Status != onlyIfStatus))
            {
                return Task.FromResult(false);
            }
            w.Status = status;
            if (incrementOfflineCount)
            {
                w.OfflineCount++;
            }
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WorkerDTO>> GetStaleAsync(DateTimeOffset heartbeatBefore, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult<IReadOnlyList<WorkerDTO>>(store.Workers.Values
                .Where(w => w.Status is WorkerStatus.Online or WorkerStatus.Draining && w.LastHeartbeatUtc < heartbeatBefore).Select(w => w.Clone()).ToList());
        }
    }
}

/// <summary>In-memory <see cref="IWorkerCommandDAO"/>.</summary>
public sealed class InMemoryWorkerCommandADO(InMemoryStore store) : IWorkerCommandDAO
{
    /// <inheritdoc />
    public Task InsertAsync(WorkerCommandDTO command, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            store.Commands[command.CommandId] = command.Clone();
            store.CommandAcks[command.CommandId] = null;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WorkerCommandDTO>> TakePendingAsync(Guid workerId, DateTimeOffset now, DateTimeOffset redeliverBefore, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            var list = store.Commands.Values
                .Where(c => c.WorkerId == workerId && store.CommandAcks[c.CommandId] is null && (c.DeliveredUtc is null || c.DeliveredUtc < redeliverBefore))
                .Take(100).ToList();
            foreach (var c in list)
            {
                c.DeliveredUtc = now;
                if (c.JobId is null)
                {
                    store.CommandAcks[c.CommandId] = now;
                }
            }
            return Task.FromResult<IReadOnlyList<WorkerCommandDTO>>(list.Select(c => c.Clone()).ToList());
        }
    }

    /// <inheritdoc />
    public Task AcknowledgeJobAsync(Guid jobId, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            foreach (var c in store.Commands.Values.Where(c => c.JobId == jobId))
            {
                store.CommandAcks[c.CommandId] ??= now;
            }
        }
        return Task.CompletedTask;
    }
}

/// <summary>In-memory <see cref="ILeaderLeaseDAO"/>.</summary>
public sealed class InMemoryLeaderLeaseADO(InMemoryStore store) : ILeaderLeaseDAO
{
    /// <inheritdoc />
    public Task<bool> TryAcquireAsync(string name, string holderId, DateTimeOffset now, DateTimeOffset expiresUtc, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            if (store.Leases.TryGetValue(name, out var l) && l.HolderId != holderId && l.ExpiresUtc >= now)
            {
                return Task.FromResult(false);
            }
            store.Leases[name] = new LeaderLeaseDTO(name, holderId, expiresUtc);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<LeaderLeaseDTO?> GetAsync(string name, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult(store.Leases.GetValueOrDefault(name));
        }
    }

    /// <inheritdoc />
    public Task ReleaseAsync(string name, string holderId, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            if (store.Leases.TryGetValue(name, out var l) && l.HolderId == holderId)
            {
                store.Leases.Remove(name);
            }
        }
        return Task.CompletedTask;
    }
}
