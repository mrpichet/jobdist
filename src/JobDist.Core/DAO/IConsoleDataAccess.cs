using JobDist.Core.Domain;

namespace JobDist.Core.DAO;

// Repository interfaces for the web console features. Same rules as IDataAccess.cs: async, cancellable,
// safe for several server instances at the same time (conditional updates, unique keys).

/// <summary>Alert recipients.</summary>
public interface IAlertRecipientDAO
{
    /// <summary>All recipients ordered by name.</summary>
    Task<IReadOnlyList<AlertRecipientDTO>> ListAsync(CancellationToken ct = default);

    /// <summary>One recipient or null.</summary>
    Task<AlertRecipientDTO?> GetAsync(Guid recipientId, CancellationToken ct = default);

    /// <summary>Inserts a recipient.</summary>
    Task InsertAsync(AlertRecipientDTO recipient, CancellationToken ct = default);

    /// <summary>Updates when the stored version equals <paramref name="expectedVersion"/>; the caller sets the new version.</summary>
    /// <returns><c>true</c> when updated.</returns>
    Task<bool> TryUpdateAsync(AlertRecipientDTO recipient, long expectedVersion, CancellationToken ct = default);

    /// <summary>Deletes when the stored version matches.</summary>
    /// <returns><c>true</c> when deleted.</returns>
    Task<bool> TryDeleteAsync(Guid recipientId, long expectedVersion, CancellationToken ct = default);
}

/// <summary>Alert outbox (delivery queue and history).</summary>
public interface IAlertOutboxDAO
{
    /// <summary>Inserts rows; a row whose (event key, recipient) already exists is skipped (idempotent producer).</summary>
    /// <returns>Rows inserted.</returns>
    Task<int> InsertIfAbsentAsync(IReadOnlyList<AlertOutboxDTO> rows, CancellationToken ct = default);

    /// <summary>
    /// Claims pending rows that are due and not locked by another instance, oldest first, at most one pending row per
    /// (subject, recipient) so alerts about the same thing are delivered in order.
    /// </summary>
    Task<IReadOnlyList<AlertOutboxDTO>> ClaimDueAsync(string instanceId, DateTimeOffset now, DateTimeOffset lockUntil, int take, CancellationToken ct = default);

    /// <summary>
    /// Claims one pending row by id for a broker consumer: succeeds when the row is still Pending and is either reserved by the
    /// relay (<c>LockedBy</c> starts with <paramref name="relayPrefix"/>) or not locked. Returns null when it was already handled.
    /// </summary>
    Task<AlertOutboxDTO?> ClaimByIdAsync(long outboxId, string instanceId, string relayPrefix, DateTimeOffset now, DateTimeOffset lockUntil, CancellationToken ct = default);

    /// <summary>Writes the result of a delivery attempt (only if the row is still locked by the caller) and releases the lock.</summary>
    Task<bool> CompleteAsync(AlertDeliveryUpdateDTO update, CancellationToken ct = default);

    /// <summary>Alert history, newest first.</summary>
    Task<(IReadOnlyList<AlertOutboxDTO> Items, int Total)> QueryAsync(AlertOutboxQueryDTO query, CancellationToken ct = default);

    /// <summary>Deletes finished rows created before a cut-off.</summary>
    Task<int> PurgeAsync(DateTimeOffset createdBefore, CancellationToken ct = default);
}

/// <summary>Queue thresholds.</summary>
public interface IQueueThresholdDAO
{
    /// <summary>All thresholds ordered by job type.</summary>
    Task<IReadOnlyList<QueueThresholdDTO>> ListAsync(CancellationToken ct = default);

    /// <summary>One threshold or null.</summary>
    Task<QueueThresholdDTO?> GetAsync(Guid thresholdId, CancellationToken ct = default);

    /// <summary>Inserts a threshold.</summary>
    /// <exception cref="Services.ConflictException">The job type already has a threshold.</exception>
    Task InsertAsync(QueueThresholdDTO threshold, CancellationToken ct = default);

    /// <summary>Updates when the stored version matches.</summary>
    Task<bool> TryUpdateAsync(QueueThresholdDTO threshold, long expectedVersion, CancellationToken ct = default);

    /// <summary>Deletes when the stored version matches.</summary>
    Task<bool> TryDeleteAsync(Guid thresholdId, long expectedVersion, CancellationToken ct = default);
}

/// <summary>Stored responses for <c>Idempotency-Key</c>.</summary>
public interface IIdempotencyRecordDAO
{
    /// <summary>Record that has not expired, or null.</summary>
    Task<IdempotencyRecordDTO?> GetAsync(string principal, string route, string idempotencyKey, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Stores a record; <c>false</c> when another request stored the same key first.</summary>
    Task<bool> TryInsertAsync(IdempotencyRecordDTO record, CancellationToken ct = default);

    /// <summary>Deletes expired records.</summary>
    Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>Aggregates for the job statistics report (grouped by UTC hour so any whole-hour time zone can be rebuilt).</summary>
public interface IJobReportDAO
{
    /// <summary>Jobs created in the range.</summary>
    Task<IReadOnlyList<SubmittedBucketDTO>> GetSubmittedAsync(JobReportQueryDTO query, CancellationToken ct = default);

    /// <summary>Jobs completed (Succeeded / Failed / Cancelled) in the range.</summary>
    Task<IReadOnlyList<CompletedBucketDTO>> GetCompletedAsync(JobReportQueryDTO query, CancellationToken ct = default);

    /// <summary>Attempts finished in the range with duration histogram.</summary>
    Task<IReadOnlyList<AttemptBucketDTO>> GetAttemptsAsync(JobReportQueryDTO query, CancellationToken ct = default);
}
