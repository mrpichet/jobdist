using System.Data.Common;
using System.Text;
using Dapper;
using JobDist.Core.DAO;
using JobDist.Core.Domain;

namespace JobDist.Infrastructure.Data;

/// <summary><see cref="IAlertRecipientDAO"/> for SQL Server and PostgreSQL.</summary>
public sealed class AlertRecipientADO(IDbConnectionFactory factory) : IAlertRecipientDAO
{
    private const string Columns =
        "RecipientId, Name, Channel, AddressProtected, AddressMasked, EventTypes, MinSeverity, Enabled, CreatedUtc, UpdatedUtc, UpdatedBy, Version";

    /// <inheritdoc />
    public async Task<IReadOnlyList<AlertRecipientDTO>> ListAsync(CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return (await c.QueryAsync<AlertRecipientDTO>(new CommandDefinition($"SELECT {Columns} FROM AlertRecipients ORDER BY Name",
            commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    /// <inheritdoc />
    public async Task<AlertRecipientDTO?> GetAsync(Guid recipientId, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<AlertRecipientDTO>(new CommandDefinition($"SELECT {Columns} FROM AlertRecipients WHERE RecipientId = @recipientId",
            new { recipientId }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task InsertAsync(AlertRecipientDTO recipient, CancellationToken ct = default)
    {
        var sql = $"INSERT INTO AlertRecipients ({Columns}) VALUES (@RecipientId, @Name, @Channel, @AddressProtected, @AddressMasked, @EventTypes, " +
                  "@MinSeverity, @Enabled, @CreatedUtc, @UpdatedUtc, @UpdatedBy, @Version)";
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        await DbErrors.WrapUniqueAsync(() => c.ExecuteAsync(new CommandDefinition(sql, recipient, commandTimeout: factory.CommandTimeout, cancellationToken: ct)),
            "This recipient already exists.").ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TryUpdateAsync(AlertRecipientDTO recipient, long expectedVersion, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE AlertRecipients SET Name = @Name, Channel = @Channel, AddressProtected = @AddressProtected, AddressMasked = @AddressMasked,
                   EventTypes = @EventTypes, MinSeverity = @MinSeverity, Enabled = @Enabled, UpdatedUtc = @UpdatedUtc, UpdatedBy = @UpdatedBy, Version = @Version
            WHERE RecipientId = @RecipientId AND Version = @expectedVersion
            """;
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return await c.ExecuteAsync(new CommandDefinition(sql, new
        {
            recipient.RecipientId, recipient.Name, recipient.Channel, recipient.AddressProtected, recipient.AddressMasked, recipient.EventTypes,
            recipient.MinSeverity, recipient.Enabled, recipient.UpdatedUtc, recipient.UpdatedBy, recipient.Version, expectedVersion
        }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async Task<bool> TryDeleteAsync(Guid recipientId, long expectedVersion, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return await c.ExecuteAsync(new CommandDefinition("DELETE FROM AlertRecipients WHERE RecipientId = @recipientId AND Version = @expectedVersion",
            new { recipientId, expectedVersion }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false) == 1;
    }
}

/// <summary>
/// <see cref="IAlertOutboxDAO"/> for SQL Server and PostgreSQL. Rows are claimed with row locks that skip rows locked by
/// other instances (<c>UPDLOCK, READPAST</c> / <c>FOR UPDATE SKIP LOCKED</c>) plus a lease column, so each alert is handled by one instance.
/// </summary>
public sealed class AlertOutboxADO(IDbConnectionFactory factory) : IAlertOutboxDAO
{
    private bool Pg => factory.Provider == DatabaseProvider.PostgreSql;

    private const string Columns =
        "OutboxId, EventKey, RecipientId, RecipientName, Channel, AddressMasked, EventType, Severity, Subject, Title, Body, OccurredUtc, CreatedUtc, " +
        "Status, Attempts, NextAttemptUtc, LockedBy, LockedUntilUtc, SentUtc, CorrelationId, LastError, InstanceId";

    private const string InsertColumns =
        "EventKey, RecipientId, RecipientName, Channel, AddressMasked, EventType, Severity, Subject, Title, Body, OccurredUtc, CreatedUtc, " +
        "Status, Attempts, NextAttemptUtc, SentUtc, LastError, InstanceId";

    private const string InsertValues =
        "@EventKey, @RecipientId, @RecipientName, @Channel, @AddressMasked, @EventType, @Severity, @Subject, @Title, @Body, @OccurredUtc, @CreatedUtc, " +
        "@Status, @Attempts, @NextAttemptUtc, @SentUtc, @LastError, @InstanceId";

    /// <inheritdoc />
    public async Task<int> InsertIfAbsentAsync(IReadOnlyList<AlertOutboxDTO> rows, CancellationToken ct = default)
    {
        var sql = Pg
            ? $"INSERT INTO AlertOutbox ({InsertColumns}) VALUES ({InsertValues}) ON CONFLICT (EventKey, RecipientId) DO NOTHING"
            : $"""
               IF NOT EXISTS (SELECT 1 FROM AlertOutbox WHERE EventKey = @EventKey AND RecipientId = @RecipientId)
                   INSERT INTO AlertOutbox ({InsertColumns}) VALUES ({InsertValues});
               """;
        var inserted = 0;
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            try
            {
                inserted += Math.Max(0, await c.ExecuteAsync(new CommandDefinition(sql, row, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false));
            }
            catch (DbException ex) when (DbErrors.IsUniqueViolation(ex))
            {
                // Another instance inserted the same event for this recipient a moment earlier: already queued.
            }
        }
        return inserted;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AlertOutboxDTO>> ClaimDueAsync(string instanceId, DateTimeOffset now, DateTimeOffset lockUntil, int take, CancellationToken ct = default)
    {
        const string due = """
            c.Status = 0 AND c.NextAttemptUtc <= @now AND (c.LockedUntilUtc IS NULL OR c.LockedUntilUtc < @now)
            AND NOT EXISTS (SELECT 1 FROM AlertOutbox p WHERE p.Subject = c.Subject AND p.RecipientId = c.RecipientId AND p.Status = 0 AND p.OutboxId < c.OutboxId)
            """;
        var sql = Pg
            ? $"""
               UPDATE AlertOutbox o SET LockedBy = @instanceId, LockedUntilUtc = @lockUntil
               WHERE o.OutboxId IN (SELECT c.OutboxId FROM AlertOutbox c WHERE {due} ORDER BY c.OutboxId LIMIT @take FOR UPDATE OF c SKIP LOCKED)
               RETURNING {Prefix("o.")}
               """
            : $"""
               WITH due AS (SELECT TOP (@take) * FROM AlertOutbox c WITH (UPDLOCK, READPAST, ROWLOCK) WHERE {due} ORDER BY c.OutboxId)
               UPDATE due SET LockedBy = @instanceId, LockedUntilUtc = @lockUntil
               OUTPUT {Prefix("inserted.")};
               """;
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<AlertOutboxDTO>(new CommandDefinition(sql, new { instanceId, now, lockUntil, take },
            commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
        return rows.OrderBy(r => r.OutboxId).ToList();
    }

    /// <inheritdoc />
    public async Task<AlertOutboxDTO?> ClaimByIdAsync(long outboxId, string instanceId, string relayPrefix, DateTimeOffset now, DateTimeOffset lockUntil, CancellationToken ct = default)
    {
        const string where = "OutboxId = @outboxId AND Status = 0 AND (LockedBy IS NULL OR LockedBy LIKE @prefix OR LockedUntilUtc < @now)";
        var sql = Pg
            ? $"UPDATE AlertOutbox SET LockedBy = @instanceId, LockedUntilUtc = @lockUntil WHERE {where} RETURNING {Columns}"
            : $"UPDATE AlertOutbox SET LockedBy = @instanceId, LockedUntilUtc = @lockUntil OUTPUT {Prefix("inserted.")} WHERE {where}";
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<AlertOutboxDTO>(new CommandDefinition(sql, new { outboxId, instanceId, prefix = relayPrefix + "%", now, lockUntil },
            commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> CompleteAsync(AlertDeliveryUpdateDTO update, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE AlertOutbox SET Status = @Status, Attempts = @Attempts, NextAttemptUtc = @NextAttemptUtc, SentUtc = @SentUtc,
                   CorrelationId = @CorrelationId, LastError = @LastError, InstanceId = @LockedBy, LockedBy = NULL, LockedUntilUtc = NULL,
                   RecipientName = COALESCE(@RecipientName, RecipientName), Channel = COALESCE(@Channel, Channel), AddressMasked = COALESCE(@AddressMasked, AddressMasked)
            WHERE OutboxId = @OutboxId AND LockedBy = @LockedBy AND Status = 0
            """;
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return await c.ExecuteAsync(new CommandDefinition(sql, new
        {
            update.OutboxId, update.LockedBy, Status = (int)update.Status, update.Attempts, update.NextAttemptUtc, update.SentUtc,
            update.CorrelationId, update.LastError, update.RecipientName, update.Channel, update.AddressMasked
        }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<AlertOutboxDTO> Items, int Total)> QueryAsync(AlertOutboxQueryDTO query, CancellationToken ct = default)
    {
        var where = new StringBuilder("WHERE CreatedUtc >= @FromUtc AND CreatedUtc < @ToUtc");
        if (query.EventType is not null)
        {
            where.Append(" AND EventType = @EventType");
        }
        if (query.Status is not null)
        {
            where.Append(" AND Status = @Status");
        }
        if (query.RecipientId is not null)
        {
            where.Append(" AND RecipientId = @RecipientId");
        }
        var page = Pg ? "LIMIT @Take OFFSET @Skip" : "OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";
        var args = new
        {
            query.FromUtc, query.ToUtc, EventType = (int?)query.EventType, Status = (int?)query.Status, query.RecipientId, query.Skip, query.Take
        };
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        var total = await c.ExecuteScalarAsync<int>(new CommandDefinition($"SELECT COUNT(*) FROM AlertOutbox {where}", args,
            commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
        var items = await c.QueryAsync<AlertOutboxDTO>(new CommandDefinition($"SELECT {Columns} FROM AlertOutbox {where} ORDER BY OutboxId DESC {page}", args,
            commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
        return (items.ToList(), total);
    }

    /// <inheritdoc />
    public async Task<int> PurgeAsync(DateTimeOffset createdBefore, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return await c.ExecuteAsync(new CommandDefinition("DELETE FROM AlertOutbox WHERE CreatedUtc < @createdBefore AND Status <> 0",
            new { createdBefore }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static string Prefix(string prefix) => string.Join(", ", Columns.Split(", ").Select(col => prefix + col));
}

/// <summary><see cref="IQueueThresholdDAO"/> for SQL Server and PostgreSQL.</summary>
public sealed class QueueThresholdADO(IDbConnectionFactory factory) : IQueueThresholdDAO
{
    private const string Columns = "ThresholdId, JobType, MaxPending, WarnPercent, Enabled, UpdatedBy, UpdatedUtc, Version";

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueueThresholdDTO>> ListAsync(CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return (await c.QueryAsync<QueueThresholdDTO>(new CommandDefinition($"SELECT {Columns} FROM QueueThresholds ORDER BY JobType",
            commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    /// <inheritdoc />
    public async Task<QueueThresholdDTO?> GetAsync(Guid thresholdId, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<QueueThresholdDTO>(new CommandDefinition($"SELECT {Columns} FROM QueueThresholds WHERE ThresholdId = @thresholdId",
            new { thresholdId }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task InsertAsync(QueueThresholdDTO threshold, CancellationToken ct = default)
    {
        var sql = $"INSERT INTO QueueThresholds ({Columns}) VALUES (@ThresholdId, @JobType, @MaxPending, @WarnPercent, @Enabled, @UpdatedBy, @UpdatedUtc, @Version)";
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        await DbErrors.WrapUniqueAsync(() => c.ExecuteAsync(new CommandDefinition(sql, threshold, commandTimeout: factory.CommandTimeout, cancellationToken: ct)),
            "This job type already has a queue threshold.").ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TryUpdateAsync(QueueThresholdDTO threshold, long expectedVersion, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE QueueThresholds SET JobType = @JobType, MaxPending = @MaxPending, WarnPercent = @WarnPercent, Enabled = @Enabled,
                   UpdatedBy = @UpdatedBy, UpdatedUtc = @UpdatedUtc, Version = @Version
            WHERE ThresholdId = @ThresholdId AND Version = @expectedVersion
            """;
        var updated = 0;
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        await DbErrors.WrapUniqueAsync(async () => updated = await c.ExecuteAsync(new CommandDefinition(sql, new
        {
            threshold.ThresholdId, threshold.JobType, threshold.MaxPending, threshold.WarnPercent, threshold.Enabled, threshold.UpdatedBy,
            threshold.UpdatedUtc, threshold.Version, expectedVersion
        }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false), "This job type already has a queue threshold.").ConfigureAwait(false);
        return updated == 1;
    }

    /// <inheritdoc />
    public async Task<bool> TryDeleteAsync(Guid thresholdId, long expectedVersion, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return await c.ExecuteAsync(new CommandDefinition("DELETE FROM QueueThresholds WHERE ThresholdId = @thresholdId AND Version = @expectedVersion",
            new { thresholdId, expectedVersion }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false) == 1;
    }
}

/// <summary><see cref="IIdempotencyRecordDAO"/> for SQL Server and PostgreSQL.</summary>
public sealed class IdempotencyRecordADO(IDbConnectionFactory factory) : IIdempotencyRecordDAO
{
    private const string Columns = "Principal, Route, IdempotencyKey, RequestHash, StatusCode, ResponseBody, CreatedUtc, ExpiresUtc";

    /// <inheritdoc />
    public async Task<IdempotencyRecordDTO?> GetAsync(string principal, string route, string idempotencyKey, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<IdempotencyRecordDTO>(new CommandDefinition(
            $"SELECT {Columns} FROM IdempotencyRecords WHERE Principal = @principal AND Route = @route AND IdempotencyKey = @idempotencyKey AND ExpiresUtc > @now",
            new { principal, route, idempotencyKey, now }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TryInsertAsync(IdempotencyRecordDTO record, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            // An expired record with the same key may still exist until housekeeping removes it.
            await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM IdempotencyRecords WHERE Principal = @Principal AND Route = @Route AND IdempotencyKey = @IdempotencyKey AND ExpiresUtc <= @CreatedUtc",
                record, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
            await c.ExecuteAsync(new CommandDefinition(
                $"INSERT INTO IdempotencyRecords ({Columns}) VALUES (@Principal, @Route, @IdempotencyKey, @RequestHash, @StatusCode, @ResponseBody, @CreatedUtc, @ExpiresUtc)",
                record, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
            return true;
        }
        catch (DbException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return await c.ExecuteAsync(new CommandDefinition("DELETE FROM IdempotencyRecords WHERE ExpiresUtc <= @now", new { now },
            commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
    }
}

/// <summary><see cref="IJobReportDAO"/> for SQL Server and PostgreSQL: aggregation happens in the database, grouped by UTC hour.</summary>
public sealed class JobReportADO(IDbConnectionFactory factory) : IJobReportDAO
{
    private bool Pg => factory.Provider == DatabaseProvider.PostgreSql;

    private string Hour(string column) => Pg
        ? $"date_trunc('hour', {column} AT TIME ZONE 'UTC')"
        : $"DATEADD(hour, DATEDIFF(hour, 0, CAST(SWITCHOFFSET({column}, '+00:00') AS DATETIME2)), CAST(0 AS DATETIME2))";

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubmittedBucketDTO>> GetSubmittedAsync(JobReportQueryDTO query, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {Hour("CreatedUtc")} AS HourUtc, JobType, AssignedWorkerId AS WorkerId, COUNT(*) AS JobCount
            FROM Jobs
            WHERE CreatedUtc >= @FromUtc AND CreatedUtc < @ToUtc {Filter("AssignedWorkerId", query)}
            GROUP BY {Hour("CreatedUtc")}, JobType, AssignedWorkerId
            """;
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return (await c.QueryAsync<SubmittedBucketDTO>(new CommandDefinition(sql, query, commandTimeout: Math.Max(factory.CommandTimeout, 120), cancellationToken: ct))
            .ConfigureAwait(false)).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CompletedBucketDTO>> GetCompletedAsync(JobReportQueryDTO query, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {Hour("CompletedUtc")} AS HourUtc, JobType, AssignedWorkerId AS WorkerId, Status, COUNT(*) AS JobCount, SUM(CAST(Attempt AS BIGINT)) AS SumAttempts
            FROM Jobs
            WHERE CompletedUtc >= @FromUtc AND CompletedUtc < @ToUtc AND Status IN (3, 4, 6) {Filter("AssignedWorkerId", query)}
            GROUP BY {Hour("CompletedUtc")}, JobType, AssignedWorkerId, Status
            """;
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return (await c.QueryAsync<CompletedBucketDTO>(new CommandDefinition(sql, query, commandTimeout: Math.Max(factory.CommandTimeout, 120), cancellationToken: ct))
            .ConfigureAwait(false)).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AttemptBucketDTO>> GetAttemptsAsync(JobReportQueryDTO query, CancellationToken ct = default)
    {
        var bucket = Pg
            ? "CAST(FLOOR(LN(COALESCE(DurationMs, 0) + 1) * 20) AS INT)"
            : "CAST(FLOOR(LOG(COALESCE(DurationMs, 0) + 1) * 20) AS INT)";
        var sql = $"""
            SELECT {Hour("FinishedUtc")} AS HourUtc, JobType, WorkerId, Outcome, {bucket} AS DurationBucket, COUNT(*) AS AttemptCount,
                   SUM(COALESCE(DurationMs, 0)) AS SumDurationMs, SUM(QueueWaitMs) AS SumQueueWaitMs
            FROM JobAttempts
            WHERE FinishedUtc >= @FromUtc AND FinishedUtc < @ToUtc {Filter("WorkerId", query)}
            GROUP BY {Hour("FinishedUtc")}, JobType, WorkerId, Outcome, {bucket}
            """;
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return (await c.QueryAsync<AttemptBucketDTO>(new CommandDefinition(sql, query, commandTimeout: Math.Max(factory.CommandTimeout, 120), cancellationToken: ct))
            .ConfigureAwait(false)).ToList();
    }

    private static string Filter(string workerColumn, JobReportQueryDTO q) =>
        (q.JobType is null ? string.Empty : " AND JobType = @JobType") + (q.WorkerId is null ? string.Empty : $" AND {workerColumn} = @WorkerId");
}
