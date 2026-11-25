using JobDist.Contracts;
using JobDist.Core.DAO;
using JobDist.Core.Domain;
using JobDist.Core.Services;

namespace JobDist.Infrastructure.Data.InMemory;

/// <summary>In-memory <see cref="IAlertRecipientDAO"/>.</summary>
public sealed class InMemoryAlertRecipientADO(InMemoryStore store) : IAlertRecipientDAO
{
    /// <inheritdoc />
    public Task<IReadOnlyList<AlertRecipientDTO>> ListAsync(CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult<IReadOnlyList<AlertRecipientDTO>>(store.Recipients.Values.OrderBy(r => r.Name).Select(r => r.Clone()).ToList());
        }
    }

    /// <inheritdoc />
    public Task<AlertRecipientDTO?> GetAsync(Guid recipientId, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult(store.Recipients.TryGetValue(recipientId, out var r) ? r.Clone() : null);
        }
    }

    /// <inheritdoc />
    public Task InsertAsync(AlertRecipientDTO recipient, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            if (!store.Recipients.TryAdd(recipient.RecipientId, recipient.Clone()))
            {
                throw new ConflictException("This recipient already exists.");
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> TryUpdateAsync(AlertRecipientDTO recipient, long expectedVersion, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            if (!store.Recipients.TryGetValue(recipient.RecipientId, out var current) || current.Version != expectedVersion)
            {
                return Task.FromResult(false);
            }
            var copy = recipient.Clone();
            copy.CreatedUtc = current.CreatedUtc;
            store.Recipients[recipient.RecipientId] = copy;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryDeleteAsync(Guid recipientId, long expectedVersion, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult(store.Recipients.TryGetValue(recipientId, out var r) && r.Version == expectedVersion && store.Recipients.Remove(recipientId));
        }
    }
}

/// <summary>In-memory <see cref="IAlertOutboxDAO"/>.</summary>
public sealed class InMemoryAlertOutboxADO(InMemoryStore store) : IAlertOutboxDAO
{
    /// <inheritdoc />
    public Task<int> InsertIfAbsentAsync(IReadOnlyList<AlertOutboxDTO> rows, CancellationToken ct = default)
    {
        var inserted = 0;
        lock (store.Gate)
        {
            foreach (var row in rows)
            {
                if (store.Outbox.Any(o => o.EventKey == row.EventKey && o.RecipientId == row.RecipientId))
                {
                    continue;
                }
                var copy = row.Clone();
                copy.OutboxId = ++store.OutboxSequence;
                store.Outbox.Add(copy);
                inserted++;
            }
        }
        return Task.FromResult(inserted);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AlertOutboxDTO>> ClaimDueAsync(string instanceId, DateTimeOffset now, DateTimeOffset lockUntil, int take, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            var pending = store.Outbox.Where(o => o.Status == (int)AlertSendStatus.Pending).ToList();
            var claimed = pending
                .Where(o => o.NextAttemptUtc <= now && (o.LockedUntilUtc is null || o.LockedUntilUtc < now))
                .Where(o => !pending.Any(p => p.Subject == o.Subject && p.RecipientId == o.RecipientId && p.OutboxId < o.OutboxId))
                .OrderBy(o => o.OutboxId).Take(take).ToList();
            foreach (var o in claimed)
            {
                o.LockedBy = instanceId;
                o.LockedUntilUtc = lockUntil;
            }
            return Task.FromResult<IReadOnlyList<AlertOutboxDTO>>(claimed.Select(o => o.Clone()).ToList());
        }
    }

    /// <inheritdoc />
    public Task<AlertOutboxDTO?> ClaimByIdAsync(long outboxId, string instanceId, string relayPrefix, DateTimeOffset now, DateTimeOffset lockUntil, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            var o = store.Outbox.FirstOrDefault(x => x.OutboxId == outboxId && x.Status == (int)AlertSendStatus.Pending
                && (x.LockedBy is null || x.LockedBy.StartsWith(relayPrefix, StringComparison.Ordinal) || x.LockedUntilUtc < now));
            if (o is null)
            {
                return Task.FromResult<AlertOutboxDTO?>(null);
            }
            o.LockedBy = instanceId;
            o.LockedUntilUtc = lockUntil;
            return Task.FromResult<AlertOutboxDTO?>(o.Clone());
        }
    }

    /// <inheritdoc />
    public Task<bool> CompleteAsync(AlertDeliveryUpdateDTO update, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            var o = store.Outbox.FirstOrDefault(x => x.OutboxId == update.OutboxId);
            if (o is null || o.LockedBy != update.LockedBy || o.Status != (int)AlertSendStatus.Pending)
            {
                return Task.FromResult(false);
            }
            o.Status = (int)update.Status;
            o.Attempts = update.Attempts;
            o.NextAttemptUtc = update.NextAttemptUtc;
            o.SentUtc = update.SentUtc;
            o.CorrelationId = update.CorrelationId;
            o.LastError = update.LastError;
            o.InstanceId = update.LockedBy;
            o.LockedBy = null;
            o.LockedUntilUtc = null;
            o.RecipientName = update.RecipientName ?? o.RecipientName;
            o.Channel = update.Channel ?? o.Channel;
            o.AddressMasked = update.AddressMasked ?? o.AddressMasked;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<(IReadOnlyList<AlertOutboxDTO> Items, int Total)> QueryAsync(AlertOutboxQueryDTO query, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            var q = store.Outbox.Where(o => o.CreatedUtc >= query.FromUtc && o.CreatedUtc < query.ToUtc
                                            && (query.EventType is null || o.EventType == (int)query.EventType)
                                            && (query.Status is null || o.Status == (int)query.Status)
                                            && (query.RecipientId is null || o.RecipientId == query.RecipientId)).ToList();
            IReadOnlyList<AlertOutboxDTO> items = q.OrderByDescending(o => o.OutboxId).Skip(query.Skip).Take(query.Take).Select(o => o.Clone()).ToList();
            return Task.FromResult((items, q.Count));
        }
    }

    /// <inheritdoc />
    public Task<int> PurgeAsync(DateTimeOffset createdBefore, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult(store.Outbox.RemoveAll(o => o.CreatedUtc < createdBefore && o.Status != (int)AlertSendStatus.Pending));
        }
    }
}

/// <summary>In-memory <see cref="IQueueThresholdDAO"/>.</summary>
public sealed class InMemoryQueueThresholdADO(InMemoryStore store) : IQueueThresholdDAO
{
    /// <inheritdoc />
    public Task<IReadOnlyList<QueueThresholdDTO>> ListAsync(CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult<IReadOnlyList<QueueThresholdDTO>>(store.Thresholds.Values.OrderBy(t => t.JobType, StringComparer.Ordinal).Select(t => t.Clone()).ToList());
        }
    }

    /// <inheritdoc />
    public Task<QueueThresholdDTO?> GetAsync(Guid thresholdId, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult(store.Thresholds.TryGetValue(thresholdId, out var t) ? t.Clone() : null);
        }
    }

    /// <inheritdoc />
    public Task InsertAsync(QueueThresholdDTO threshold, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            if (store.Thresholds.Values.Any(t => string.Equals(t.JobType, threshold.JobType, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ConflictException("This job type already has a queue threshold.");
            }
            store.Thresholds[threshold.ThresholdId] = threshold.Clone();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> TryUpdateAsync(QueueThresholdDTO threshold, long expectedVersion, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            if (!store.Thresholds.TryGetValue(threshold.ThresholdId, out var current) || current.Version != expectedVersion)
            {
                return Task.FromResult(false);
            }
            if (store.Thresholds.Values.Any(t => t.ThresholdId != threshold.ThresholdId && string.Equals(t.JobType, threshold.JobType, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ConflictException("This job type already has a queue threshold.");
            }
            store.Thresholds[threshold.ThresholdId] = threshold.Clone();
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryDeleteAsync(Guid thresholdId, long expectedVersion, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult(store.Thresholds.TryGetValue(thresholdId, out var t) && t.Version == expectedVersion && store.Thresholds.Remove(thresholdId));
        }
    }
}

/// <summary>In-memory <see cref="IIdempotencyRecordDAO"/>.</summary>
public sealed class InMemoryIdempotencyRecordADO(InMemoryStore store) : IIdempotencyRecordDAO
{
    /// <inheritdoc />
    public Task<IdempotencyRecordDTO?> GetAsync(string principal, string route, string idempotencyKey, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            return Task.FromResult(store.Idempotency.TryGetValue((principal, route, idempotencyKey), out var r) && r.ExpiresUtc > now ? r : null);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryInsertAsync(IdempotencyRecordDTO record, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            var key = (record.Principal, record.Route, record.IdempotencyKey);
            if (store.Idempotency.TryGetValue(key, out var existing) && existing.ExpiresUtc > record.CreatedUtc)
            {
                return Task.FromResult(false);
            }
            store.Idempotency[key] = record;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            var expired = store.Idempotency.Where(kv => kv.Value.ExpiresUtc <= now).Select(kv => kv.Key).ToList();
            expired.ForEach(k => store.Idempotency.Remove(k));
            return Task.FromResult(expired.Count);
        }
    }
}

/// <summary>In-memory <see cref="IJobReportDAO"/> (same grouping as the SQL implementation).</summary>
public sealed class InMemoryJobReportADO(InMemoryStore store) : IJobReportDAO
{
    private static DateTime Hour(DateTimeOffset t)
    {
        var u = t.UtcDateTime;
        return new DateTime(u.Year, u.Month, u.Day, u.Hour, 0, 0, DateTimeKind.Utc);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SubmittedBucketDTO>> GetSubmittedAsync(JobReportQueryDTO query, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            IReadOnlyList<SubmittedBucketDTO> rows = store.Jobs.Values
                .Where(j => j.CreatedUtc >= query.FromUtc && j.CreatedUtc < query.ToUtc && Match(j.JobType, j.AssignedWorkerId, query))
                .GroupBy(j => (Hour(j.CreatedUtc), j.JobType, j.AssignedWorkerId))
                .Select(g => new SubmittedBucketDTO { HourUtc = g.Key.Item1, JobType = g.Key.JobType, WorkerId = g.Key.AssignedWorkerId, JobCount = g.Count() })
                .ToList();
            return Task.FromResult(rows);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CompletedBucketDTO>> GetCompletedAsync(JobReportQueryDTO query, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            IReadOnlyList<CompletedBucketDTO> rows = store.Jobs.Values
                .Where(j => j.IsTerminal && j.CompletedUtc >= query.FromUtc && j.CompletedUtc < query.ToUtc && Match(j.JobType, j.AssignedWorkerId, query))
                .GroupBy(j => (Hour(j.CompletedUtc!.Value), j.JobType, j.AssignedWorkerId, j.Status))
                .Select(g => new CompletedBucketDTO
                {
                    HourUtc = g.Key.Item1, JobType = g.Key.JobType, WorkerId = g.Key.AssignedWorkerId, Status = (int)g.Key.Status,
                    JobCount = g.Count(), SumAttempts = g.Sum(j => (long)j.Attempt)
                })
                .ToList();
            return Task.FromResult(rows);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AttemptBucketDTO>> GetAttemptsAsync(JobReportQueryDTO query, CancellationToken ct = default)
    {
        lock (store.Gate)
        {
            IReadOnlyList<AttemptBucketDTO> rows = store.Attempts.Values
                .Where(a => a.FinishedUtc >= query.FromUtc && a.FinishedUtc < query.ToUtc && Match(a.JobType, a.WorkerId, query))
                .GroupBy(a => (Hour(a.FinishedUtc!.Value), a.JobType, a.WorkerId, a.Outcome, (int)Math.Floor(Math.Log((a.DurationMs ?? 0) + 1) * 20)))
                .Select(g => new AttemptBucketDTO
                {
                    HourUtc = g.Key.Item1, JobType = g.Key.JobType, WorkerId = g.Key.WorkerId, Outcome = (int)g.Key.Outcome, DurationBucket = g.Key.Item5,
                    AttemptCount = g.Count(), SumDurationMs = g.Sum(a => a.DurationMs ?? 0), SumQueueWaitMs = g.Sum(a => a.QueueWaitMs)
                })
                .ToList();
            return Task.FromResult(rows);
        }
    }

    private static bool Match(string jobType, Guid? workerId, JobReportQueryDTO q) =>
        (q.JobType is null || jobType == q.JobType) && (q.WorkerId is null || workerId == q.WorkerId);
}
