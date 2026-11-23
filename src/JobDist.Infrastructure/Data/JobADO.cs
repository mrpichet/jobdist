using System.Data;
using System.Text.Json;
using Dapper;
using JobDist.Contracts;
using JobDist.Core.DAO;
using JobDist.Core.Domain;
using Microsoft.Data.SqlClient;
using Npgsql;
using NpgsqlTypes;

namespace JobDist.Infrastructure.Data;

/// <summary>
/// <see cref="IJobDAO"/> for SQL Server and PostgreSQL with Dapper. All statements are parameterised.
/// Multi-instance safety: assignments and updates are conditional on status + version; queue reads use
/// <c>READPAST</c> (SQL Server) / <c>SKIP LOCKED</c> (PostgreSQL) so concurrent instances never block each other.
/// Bulk inserts use <see cref="SqlBulkCopy"/> / PostgreSQL binary COPY for high-throughput ingestion.
/// </summary>
public sealed class JobADO(IDbConnectionFactory factory) : IJobDAO
{
    private bool Pg => factory.Provider == DatabaseProvider.PostgreSql;

    private const string Columns =
        "JobId, JobType, Payload, ProtectedPayload, PayloadSha256, PayloadSize, Status, Priority, TargetWorkerId, StrictTarget, " +
        "AssignedWorkerId, CurrentAttemptId, Attempt, MaxAttempts, TimeoutSeconds, CreatedUtc, RunAtUtc, NextAttemptUtc, AssignedUtc, " +
        "DeliveredUtc, LeaseExpiresUtc, StartedUtc, CompletedUtc, LastDurationMs, Result, LastError, CancelReason, LastFailedWorkerId, " +
        "IdempotencyKey, SubmittedBy, Sensitive, Tags, Version";

    private const string Values =
        "@JobId, @JobType, @Payload, @ProtectedPayload, @PayloadSha256, @PayloadSize, @Status, @Priority, @TargetWorkerId, @StrictTarget, " +
        "@AssignedWorkerId, @CurrentAttemptId, @Attempt, @MaxAttempts, @TimeoutSeconds, @CreatedUtc, @RunAtUtc, @NextAttemptUtc, @AssignedUtc, " +
        "@DeliveredUtc, @LeaseExpiresUtc, @StartedUtc, @CompletedUtc, @LastDurationMs, @Result, @LastError, @CancelReason, @LastFailedWorkerId, " +
        "@IdempotencyKey, @SubmittedBy, @Sensitive, @Tags, @Version";

    private const string HeldStatuses = "(1, 2, 5)"; // Assigned, Running, CancelRequested

    /// <inheritdoc />
    public async Task InsertAsync(JobDTO job, CancellationToken ct = default)
    {
        // Step 1: single parameterised insert; Step 2: unique idempotency index violation → ConflictException.
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        await DbErrors.WrapUniqueAsync(
            () => c.ExecuteAsync(new CommandDefinition($"INSERT INTO Jobs ({Columns}) VALUES ({Values})", Param(job), commandTimeout: factory.CommandTimeout, cancellationToken: ct)),
            "A job with this idempotency key already exists.").ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task InsertBatchAsync(IReadOnlyList<JobDTO> jobs, CancellationToken ct = default)
    {
        if (jobs.Count == 0)
        {
            return;
        }
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        if (Pg)
        {
            await PostgresCopyAsync((NpgsqlConnection)c, jobs, ct).ConfigureAwait(false);
        }
        else
        {
            await SqlBulkCopyAsync((SqlConnection)c, jobs, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<JobDTO?> GetAsync(Guid jobId, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<JobDTO>(new CommandDefinition($"SELECT {Columns} FROM Jobs WHERE JobId = @jobId", new { jobId }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<JobDTO?> GetByIdempotencyKeyAsync(string submittedBy, string idempotencyKey, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<JobDTO>(new CommandDefinition(
            $"SELECT {Columns} FROM Jobs WHERE SubmittedBy = @submittedBy AND IdempotencyKey = @idempotencyKey",
            new { submittedBy, idempotencyKey }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<JobDTO> Items, int Total)> QueryAsync(JobQueryDTO query, CancellationToken ct = default)
    {
        // Step 1: optional filters as parameters (no string concatenation of user values).
        const string where = "WHERE (@status IS NULL OR Status = @status) AND (@jobType IS NULL OR JobType = @jobType) AND (@workerId IS NULL OR AssignedWorkerId = @workerId)";
        var page = Pg
            ? $"SELECT {Columns} FROM Jobs {where} ORDER BY CreatedUtc DESC OFFSET @skip LIMIT @take"
            : $"SELECT {Columns} FROM Jobs {where} ORDER BY CreatedUtc DESC OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY";
        var p = new DynamicParameters();
        p.Add("status", query.Status is null ? null : (int?)query.Status, DbType.Int32);
        p.Add("jobType", query.JobType, DbType.String);
        p.Add("workerId", query.WorkerId, DbType.Guid);
        p.Add("skip", query.Skip);
        p.Add("take", query.Take);

        // Step 2: page + total.
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        var items = (await c.QueryAsync<JobDTO>(new CommandDefinition(page, p, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false)).ToList();
        var total = await c.ExecuteScalarAsync<int>(new CommandDefinition($"SELECT COUNT(*) FROM Jobs {where}", p, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
        return (items, total);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobDTO>> GetReadyPendingAsync(DateTimeOffset now, int take, double agingPointsPerMinute, CancellationToken ct = default)
    {
        // Effective priority = Priority + min(100, waited minutes × aging) computed in the database so old low-priority jobs
        // eventually enter the batch even under a constant stream of high-priority work (anti-starvation).
        var sql = Pg
            ? $"""
               SELECT {Columns} FROM Jobs
               WHERE Status = 0 AND NextAttemptUtc <= @now
               ORDER BY Priority + LEAST(100, EXTRACT(EPOCH FROM (@now - NextAttemptUtc)) / 60.0 * @aging) DESC, NextAttemptUtc
               LIMIT @take
               """
            : $"""
               SELECT TOP (@take) {Columns} FROM Jobs WITH (READPAST)
               WHERE Status = 0 AND NextAttemptUtc <= @now
               ORDER BY Priority + IIF(DATEDIFF(SECOND, NextAttemptUtc, @now) / 60.0 * @aging > 100, 100, DATEDIFF(SECOND, NextAttemptUtc, @now) / 60.0 * @aging) DESC, NextAttemptUtc
               """;
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return (await c.QueryAsync<JobDTO>(new CommandDefinition(sql, new { now, take, aging = agingPointsPerMinute }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssignmentDTO>> ApplyAssignmentsAsync(IReadOnlyList<AssignmentDTO> assignments, DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken ct = default)
    {
        if (assignments.Count == 0)
        {
            return [];
        }

        // Step 1: all decisions travel as one JSON parameter (one round trip, one cached plan, no 2,100-parameter limit).
        var json = JsonSerializer.Serialize(assignments);

        // Step 2: conditional update (still Pending with the expected version) + attempt rows, in one statement / transaction.
        var sql = Pg
            ? """
              WITH a AS (
                  SELECT * FROM jsonb_to_recordset(CAST(@json AS jsonb))
                      AS x("JobId" uuid, "ExpectedVersion" bigint, "WorkerId" uuid, "AttemptId" uuid, "Score" float8, "WorkerLoad" float8)),
              upd AS (
                  UPDATE Jobs j SET Status = 1, AssignedWorkerId = a."WorkerId", CurrentAttemptId = a."AttemptId", Attempt = j.Attempt + 1,
                         AssignedUtc = @now, DeliveredUtc = NULL, LeaseExpiresUtc = @lease, Version = j.Version + 1
                  FROM a WHERE j.JobId = a."JobId" AND j.Status = 0 AND j.Version = a."ExpectedVersion"
                  RETURNING j.JobId, j.Attempt, j.JobType, j.Priority, j.PayloadSize, j.NextAttemptUtc, j.CreatedUtc,
                            a."WorkerId" AS WorkerId, a."AttemptId" AS AttemptId, a."Score" AS Score, a."WorkerLoad" AS WorkerLoad, a."ExpectedVersion" AS ExpectedVersion),
              ins AS (
                  INSERT INTO JobAttempts (AttemptId, JobId, Attempt, WorkerId, JobType, Priority, PayloadSize, AssignedUtc, QueueWaitMs, Outcome, SchedulerScore, WorkerLoadAtAssign)
                  SELECT AttemptId, JobId, Attempt, WorkerId, JobType, Priority, PayloadSize, @now,
                         GREATEST(0, EXTRACT(EPOCH FROM (@now - GREATEST(NextAttemptUtc, CreatedUtc))) * 1000), 0, Score, WorkerLoad
                  FROM upd RETURNING JobId)
              SELECT JobId, ExpectedVersion, WorkerId, AttemptId, Score, WorkerLoad FROM upd
              """
            : """
              SET XACT_ABORT ON;
              BEGIN TRANSACTION;
              DECLARE @applied TABLE (JobId UNIQUEIDENTIFIER, ExpectedVersion BIGINT, WorkerId UNIQUEIDENTIFIER, AttemptId UNIQUEIDENTIFIER, Score FLOAT, WorkerLoad FLOAT,
                                      Attempt INT, JobType NVARCHAR(100), Priority INT, PayloadSize INT, ReadySince DATETIMEOFFSET(3));
              UPDATE j SET Status = 1, AssignedWorkerId = a.WorkerId, CurrentAttemptId = a.AttemptId, Attempt = j.Attempt + 1,
                           AssignedUtc = @now, DeliveredUtc = NULL, LeaseExpiresUtc = @lease, Version = j.Version + 1
              OUTPUT inserted.JobId, a.ExpectedVersion, a.WorkerId, a.AttemptId, a.Score, a.WorkerLoad, inserted.Attempt, inserted.JobType, inserted.Priority, inserted.PayloadSize,
                     IIF(inserted.NextAttemptUtc > inserted.CreatedUtc, inserted.NextAttemptUtc, inserted.CreatedUtc)
              INTO @applied
              FROM Jobs j WITH (ROWLOCK)
              JOIN OPENJSON(@json) WITH (JobId UNIQUEIDENTIFIER, ExpectedVersion BIGINT, WorkerId UNIQUEIDENTIFIER, AttemptId UNIQUEIDENTIFIER, Score FLOAT, WorkerLoad FLOAT) a
                ON a.JobId = j.JobId
              WHERE j.Status = 0 AND j.Version = a.ExpectedVersion;
              INSERT INTO JobAttempts (AttemptId, JobId, Attempt, WorkerId, JobType, Priority, PayloadSize, AssignedUtc, QueueWaitMs, Outcome, SchedulerScore, WorkerLoadAtAssign)
              SELECT AttemptId, JobId, Attempt, WorkerId, JobType, Priority, PayloadSize, @now, IIF(ReadySince > @now, 0, DATEDIFF_BIG(MILLISECOND, ReadySince, @now)), 0, Score, WorkerLoad
              FROM @applied;
              COMMIT;
              SELECT JobId, ExpectedVersion, WorkerId, AttemptId, Score, WorkerLoad FROM @applied;
              """;
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<AppliedRow>(new CommandDefinition(sql, new { json, now, lease = leaseUntil }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => new AssignmentDTO(r.JobId, r.ExpectedVersion, r.WorkerId, r.AttemptId, r.Score, r.WorkerLoad)).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobDTO>> TakeUndeliveredAsync(Guid workerId, int max, DateTimeOffset now, CancellationToken ct = default)
    {
        // Claim-and-mark in one statement; concurrent polls of the same worker skip each other's rows.
        var sql = Pg
            ? $"""
               UPDATE Jobs SET DeliveredUtc = @now
               WHERE JobId IN (SELECT JobId FROM Jobs WHERE AssignedWorkerId = @workerId AND Status IN (1, 5) AND DeliveredUtc IS NULL
                               ORDER BY Priority DESC LIMIT @max FOR UPDATE SKIP LOCKED)
               RETURNING {Columns}
               """
            : $"""
               UPDATE TOP (@max) Jobs WITH (ROWLOCK, READPAST) SET DeliveredUtc = @now
               OUTPUT {Prefixed("inserted.")}
               WHERE AssignedWorkerId = @workerId AND Status IN (1, 5) AND DeliveredUtc IS NULL
               """;
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return (await c.QueryAsync<JobDTO>(new CommandDefinition(sql, new { workerId, max, now }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> TryUpdateAsync(JobDTO job, CancellationToken ct = default)
    {
        // Optimistic concurrency: the row changes only if nobody else changed it since it was read.
        const string sql = """
            UPDATE Jobs SET Status = @Status, Priority = @Priority, AssignedWorkerId = @AssignedWorkerId, CurrentAttemptId = @CurrentAttemptId,
                Attempt = @Attempt, NextAttemptUtc = @NextAttemptUtc, AssignedUtc = @AssignedUtc, DeliveredUtc = @DeliveredUtc,
                LeaseExpiresUtc = @LeaseExpiresUtc, StartedUtc = @StartedUtc, CompletedUtc = @CompletedUtc, LastDurationMs = @LastDurationMs,
                Result = @Result, LastError = @LastError, CancelReason = @CancelReason, LastFailedWorkerId = @LastFailedWorkerId,
                Version = Version + 1
            WHERE JobId = @JobId AND Version = @Version
            """;
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await c.ExecuteAsync(new CommandDefinition(sql, Param(job), commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
        return rows == 1;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobDTO>> GetHeldByWorkerAsync(Guid workerId, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return (await c.QueryAsync<JobDTO>(new CommandDefinition(
            $"SELECT {Columns} FROM Jobs WHERE AssignedWorkerId = @workerId AND Status IN {HeldStatuses}",
            new { workerId }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobDTO>> GetExpiredLeasesAsync(DateTimeOffset now, int take, CancellationToken ct = default)
    {
        var sql = Pg
            ? $"SELECT {Columns} FROM Jobs WHERE LeaseExpiresUtc < @now AND Status IN {HeldStatuses} ORDER BY LeaseExpiresUtc LIMIT @take"
            : $"SELECT TOP (@take) {Columns} FROM Jobs WITH (READPAST) WHERE LeaseExpiresUtc < @now AND Status IN {HeldStatuses} ORDER BY LeaseExpiresUtc";
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return (await c.QueryAsync<JobDTO>(new CommandDefinition(sql, new { now, take }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkerLoadDTO>> GetActiveLoadAsync(CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<LoadRow>(new CommandDefinition(
            $"SELECT AssignedWorkerId AS WorkerId, JobType, CAST(COUNT(*) AS BIGINT) AS Active FROM Jobs WHERE AssignedWorkerId IS NOT NULL AND Status IN {HeldStatuses} GROUP BY AssignedWorkerId, JobType",
            commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => new WorkerLoadDTO(r.WorkerId, r.JobType, (int)r.Active)).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<JobStatus, int>> CountByStatusAsync(CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<CountRow>(new CommandDefinition("SELECT CAST(Status AS VARCHAR(10)) AS GroupKey, CAST(COUNT(*) AS BIGINT) AS Count FROM Jobs GROUP BY Status", commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToDictionary(r => (JobStatus)int.Parse(r.GroupKey, System.Globalization.CultureInfo.InvariantCulture), r => (int)r.Count);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, int>> PendingByTypeAsync(CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<CountRow>(new CommandDefinition("SELECT JobType AS GroupKey, CAST(COUNT(*) AS BIGINT) AS Count FROM Jobs WHERE Status = 0 GROUP BY JobType", commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToDictionary(r => r.GroupKey, r => (int)r.Count, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DateTimeOffset>> GetSubmissionTimesAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, int max, CancellationToken ct = default)
    {
        var sql = Pg
            ? "SELECT CreatedUtc FROM Jobs WHERE CreatedUtc >= @fromUtc AND CreatedUtc < @toUtc ORDER BY CreatedUtc DESC LIMIT @max"
            : "SELECT TOP (@max) CreatedUtc FROM Jobs WHERE CreatedUtc >= @fromUtc AND CreatedUtc < @toUtc ORDER BY CreatedUtc DESC";
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return (await c.QueryAsync<DateTimeOffset>(new CommandDefinition(sql, new { fromUtc, toUtc, max }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    /// <inheritdoc />
    public async Task<int> PurgeAsync(DateTimeOffset completedBefore, int batchSize, CancellationToken ct = default)
    {
        // Small batches keep locks short; attempts are removed by ON DELETE CASCADE.
        var sql = Pg
            ? "DELETE FROM Jobs WHERE JobId IN (SELECT JobId FROM Jobs WHERE CompletedUtc < @completedBefore AND Status IN (3, 4, 6) LIMIT @batchSize)"
            : "DELETE TOP (@batchSize) FROM Jobs WHERE CompletedUtc < @completedBefore AND Status IN (3, 4, 6)";
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return await c.ExecuteAsync(new CommandDefinition(sql, new { completedBefore, batchSize }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>Dapper parameters for a job (enums as int, lists via the JSON type handler).</summary>
    private static object Param(JobDTO j) => new
    {
        j.JobId, j.JobType, j.Payload, j.ProtectedPayload, j.PayloadSha256, j.PayloadSize, Status = (int)j.Status, j.Priority, j.TargetWorkerId,
        j.StrictTarget, j.AssignedWorkerId, j.CurrentAttemptId, j.Attempt, j.MaxAttempts, j.TimeoutSeconds, j.CreatedUtc, j.RunAtUtc, j.NextAttemptUtc,
        j.AssignedUtc, j.DeliveredUtc, j.LeaseExpiresUtc, j.StartedUtc, j.CompletedUtc, j.LastDurationMs, j.Result, j.LastError, j.CancelReason,
        j.LastFailedWorkerId, j.IdempotencyKey, j.SubmittedBy, j.Sensitive, j.Tags, j.Version
    };

    private static string Prefixed(string prefix) => string.Join(", ", Columns.Split(", ").Select(col => prefix + col));

    /// <summary>SQL Server bulk insert (minimal logging, one round trip per 5,000 rows).</summary>
    private static async Task SqlBulkCopyAsync(SqlConnection c, IReadOnlyList<JobDTO> jobs, CancellationToken ct)
    {
        // Step 1: in-memory table with the exact column order of the target table.
        var table = new DataTable();
        foreach (var col in Columns.Split(", "))
        {
            table.Columns.Add(col, BulkType(col));
        }
        foreach (var j in jobs)
        {
            table.Rows.Add(j.JobId, j.JobType, (object?)j.Payload ?? DBNull.Value, (object?)j.ProtectedPayload ?? DBNull.Value, j.PayloadSha256, j.PayloadSize,
                (int)j.Status, j.Priority, (object?)j.TargetWorkerId ?? DBNull.Value, j.StrictTarget, DBNull.Value, DBNull.Value, j.Attempt, j.MaxAttempts,
                j.TimeoutSeconds, j.CreatedUtc, (object?)j.RunAtUtc ?? DBNull.Value, j.NextAttemptUtc, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value,
                DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, (object?)j.IdempotencyKey ?? DBNull.Value,
                (object?)j.SubmittedBy ?? DBNull.Value, j.Sensitive, JsonSerializer.Serialize(j.Tags), j.Version);
        }

        // Step 2: bulk copy inside a transaction (all-or-nothing), unique violations → ConflictException.
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        using var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.CheckConstraints, tx) { DestinationTableName = "dbo.Jobs", BatchSize = 5_000 };
        foreach (DataColumn col in table.Columns)
        {
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        }
        await DbErrors.WrapUniqueAsync(() => bulk.WriteToServerAsync(table, ct), "A job with this idempotency key already exists.").ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static Type BulkType(string column) => column switch
    {
        "JobId" or "TargetWorkerId" or "AssignedWorkerId" or "CurrentAttemptId" or "LastFailedWorkerId" => typeof(Guid),
        "PayloadSize" or "Status" or "Priority" or "Attempt" or "MaxAttempts" or "TimeoutSeconds" => typeof(int),
        "StrictTarget" or "Sensitive" => typeof(bool),
        "LastDurationMs" => typeof(double),
        "Version" => typeof(long),
        _ when column.EndsWith("Utc", StringComparison.Ordinal) => typeof(DateTimeOffset),
        _ => typeof(string)
    };

    /// <summary>PostgreSQL binary COPY (fastest ingestion path).</summary>
    private static async Task PostgresCopyAsync(NpgsqlConnection c, IReadOnlyList<JobDTO> jobs, CancellationToken ct)
    {
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using (var writer = await c.BeginBinaryImportAsync(
                "COPY jobs (jobid, jobtype, payload, protectedpayload, payloadsha256, payloadsize, status, priority, targetworkerid, stricttarget, attempt, maxattempts, " +
                "timeoutseconds, createdutc, runatutc, nextattemptutc, idempotencykey, submittedby, sensitive, tags, version) FROM STDIN (FORMAT BINARY)", ct).ConfigureAwait(false))
            {
                foreach (var j in jobs)
                {
                    await writer.StartRowAsync(ct).ConfigureAwait(false);
                    await writer.WriteAsync(j.JobId, NpgsqlDbType.Uuid, ct).ConfigureAwait(false);
                    await writer.WriteAsync(j.JobType, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
                    await WriteNullableAsync(writer, j.Payload, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                    await WriteNullableAsync(writer, j.ProtectedPayload, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                    await writer.WriteAsync(j.PayloadSha256, NpgsqlDbType.Char, ct).ConfigureAwait(false);
                    await writer.WriteAsync(j.PayloadSize, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                    await writer.WriteAsync((int)j.Status, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                    await writer.WriteAsync(j.Priority, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                    if (j.TargetWorkerId is { } t)
                    {
                        await writer.WriteAsync(t, NpgsqlDbType.Uuid, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await writer.WriteNullAsync(ct).ConfigureAwait(false);
                    }
                    await writer.WriteAsync(j.StrictTarget, NpgsqlDbType.Boolean, ct).ConfigureAwait(false);
                    await writer.WriteAsync(j.Attempt, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                    await writer.WriteAsync(j.MaxAttempts, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                    await writer.WriteAsync(j.TimeoutSeconds, NpgsqlDbType.Integer, ct).ConfigureAwait(false);
                    await writer.WriteAsync(j.CreatedUtc.UtcDateTime, NpgsqlDbType.TimestampTz, ct).ConfigureAwait(false);
                    if (j.RunAtUtc is { } r)
                    {
                        await writer.WriteAsync(r.UtcDateTime, NpgsqlDbType.TimestampTz, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await writer.WriteNullAsync(ct).ConfigureAwait(false);
                    }
                    await writer.WriteAsync(j.NextAttemptUtc.UtcDateTime, NpgsqlDbType.TimestampTz, ct).ConfigureAwait(false);
                    await WriteNullableAsync(writer, j.IdempotencyKey, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
                    await WriteNullableAsync(writer, j.SubmittedBy, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
                    await writer.WriteAsync(j.Sensitive, NpgsqlDbType.Boolean, ct).ConfigureAwait(false);
                    await writer.WriteAsync(JsonSerializer.Serialize(j.Tags), NpgsqlDbType.Text, ct).ConfigureAwait(false);
                    await writer.WriteAsync(j.Version, NpgsqlDbType.Bigint, ct).ConfigureAwait(false);
                }
                await writer.CompleteAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            throw new Core.Services.ConflictException("A job with this idempotency key already exists.");
        }
    }

    private static Task WriteNullableAsync(NpgsqlBinaryImporter writer, string? value, NpgsqlDbType type, CancellationToken ct) =>
        value is null ? writer.WriteNullAsync(ct) : writer.WriteAsync(value, type, ct);

    // Row classes with settable properties: Dapper converts numeric widths (int/bigint) and is case-insensitive on names.
    private sealed class AppliedRow
    {
        public Guid JobId { get; set; }
        public long ExpectedVersion { get; set; }
        public Guid WorkerId { get; set; }
        public Guid AttemptId { get; set; }
        public double Score { get; set; }
        public double WorkerLoad { get; set; }
    }

    private sealed class LoadRow
    {
        public Guid WorkerId { get; set; }
        public string JobType { get; set; } = string.Empty;
        public long Active { get; set; }
    }
}
