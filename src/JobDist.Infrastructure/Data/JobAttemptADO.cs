using Dapper;
using JobDist.Core.DAO;
using JobDist.Core.Domain;

namespace JobDist.Infrastructure.Data;

/// <summary><see cref="IJobAttemptDAO"/> for SQL Server and PostgreSQL (distribution history + timing statistics).</summary>
public sealed class JobAttemptADO(IDbConnectionFactory factory) : IJobAttemptDAO
{
    private bool Pg => factory.Provider == DatabaseProvider.PostgreSql;

    private const string Columns =
        "AttemptId, JobId, Attempt, WorkerId, JobType, Priority, PayloadSize, AssignedUtc, StartedUtc, FinishedUtc, DurationMs, QueueWaitMs, " +
        "Outcome, Error, SchedulerScore, WorkerLoadAtAssign";

    /// <inheritdoc />
    public async Task<JobAttemptDTO?> GetAsync(Guid attemptId, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<JobAttemptDTO>(new CommandDefinition($"SELECT {Columns} FROM JobAttempts WHERE AttemptId = @attemptId",
            new { attemptId }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(JobAttemptDTO attempt, CancellationToken ct = default)
    {
        const string sql = "UPDATE JobAttempts SET StartedUtc = @StartedUtc, FinishedUtc = @FinishedUtc, DurationMs = @DurationMs, Outcome = @Outcome, Error = @Error WHERE AttemptId = @AttemptId";
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        await c.ExecuteAsync(new CommandDefinition(sql, new
        {
            attempt.AttemptId, attempt.StartedUtc, attempt.FinishedUtc, attempt.DurationMs, Outcome = (int)attempt.Outcome, attempt.Error
        }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobAttemptDTO>> ListByJobAsync(Guid jobId, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return (await c.QueryAsync<JobAttemptDTO>(new CommandDefinition($"SELECT {Columns} FROM JobAttempts WHERE JobId = @jobId ORDER BY Attempt, AssignedUtc",
            new { jobId }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobAttemptDTO>> GetFinishedSinceAsync(DateTimeOffset sinceUtc, int max, CancellationToken ct = default)
    {
        var sql = Pg
            ? $"SELECT {Columns} FROM JobAttempts WHERE FinishedUtc >= @sinceUtc ORDER BY FinishedUtc DESC LIMIT @max"
            : $"SELECT TOP (@max) {Columns} FROM JobAttempts WHERE FinishedUtc >= @sinceUtc ORDER BY FinishedUtc DESC";
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return (await c.QueryAsync<JobAttemptDTO>(new CommandDefinition(sql, new { sinceUtc, max }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PerformanceStatDTO>> GetStatsAsync(DateTimeOffset sinceUtc, CancellationToken ct = default)
    {
        // Aggregation runs in the database (only one row per worker/type travels over the network).
        // Outcomes: 1 Succeeded, 2 Failed, 3 TimedOut, 5 WorkerLost count as samples; Cancelled / Released are ignored.
        var stddev = Pg ? "stddev_pop" : "STDEVP";
        var sql = $"""
            SELECT WorkerId, JobType,
                   CAST(COUNT(*) AS BIGINT) AS Samples,
                   CAST(SUM(CASE WHEN Outcome = 1 THEN 1 ELSE 0 END) AS BIGINT) AS Successes,
                   CAST(SUM(CASE WHEN Outcome <> 1 THEN 1 ELSE 0 END) AS BIGINT) AS Failures,
                   COALESCE(AVG(CASE WHEN Outcome = 1 THEN DurationMs END), 0) AS MeanDurationMs,
                   COALESCE({stddev}(CASE WHEN Outcome = 1 THEN DurationMs END), 0) AS StdDevDurationMs,
                   MAX(FinishedUtc) AS LastUpdatedUtc
            FROM JobAttempts
            WHERE FinishedUtc >= @sinceUtc AND Outcome IN (1, 2, 3, 5)
            GROUP BY WorkerId, JobType
            """;
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<StatRow>(new CommandDefinition(sql, new { sinceUtc }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => new PerformanceStatDTO(r.WorkerId, r.JobType, (int)r.Samples, (int)r.Successes, (int)r.Failures, r.MeanDurationMs, r.StdDevDurationMs, r.LastUpdatedUtc)).ToList();
    }

    private sealed class StatRow
    {
        public Guid WorkerId { get; set; }
        public string JobType { get; set; } = string.Empty;
        public long Samples { get; set; }
        public long Successes { get; set; }
        public long Failures { get; set; }
        public double MeanDurationMs { get; set; }
        public double StdDevDurationMs { get; set; }
        public DateTimeOffset LastUpdatedUtc { get; set; }
    }
}
