using Dapper;
using JobDist.Core.DAO;
using JobDist.Core.Domain;

namespace JobDist.Infrastructure.Data;

/// <summary><see cref="IWorkerCommandDAO"/> for SQL Server and PostgreSQL (at-least-once delivery of cancel / drain).</summary>
public sealed class WorkerCommandADO(IDbConnectionFactory factory) : IWorkerCommandDAO
{
    private bool Pg => factory.Provider == DatabaseProvider.PostgreSql;
    private const string Columns = "CommandId, WorkerId, Kind, JobId, Reason, IssuedUtc, DeliveredUtc";

    /// <inheritdoc />
    public async Task InsertAsync(WorkerCommandDTO command, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        await c.ExecuteAsync(new CommandDefinition(
            "INSERT INTO WorkerCommands (CommandId, WorkerId, Kind, JobId, Reason, IssuedUtc) VALUES (@CommandId, @WorkerId, @Kind, @JobId, @Reason, @IssuedUtc)",
            new { command.CommandId, command.WorkerId, Kind = (int)command.Kind, command.JobId, command.Reason, command.IssuedUtc },
            commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkerCommandDTO>> TakePendingAsync(Guid workerId, DateTimeOffset now, DateTimeOffset redeliverBefore, CancellationToken ct = default)
    {
        // Commands without a job (drain) are one-shot: acknowledged when delivered. Job commands stay until the job ends.
        var sql = Pg
            ? $"""
               UPDATE WorkerCommands SET DeliveredUtc = @now, AckedUtc = CASE WHEN JobId IS NULL THEN @now ELSE AckedUtc END
               WHERE CommandId IN (SELECT CommandId FROM WorkerCommands WHERE WorkerId = @workerId AND AckedUtc IS NULL
                                   AND (DeliveredUtc IS NULL OR DeliveredUtc < @redeliverBefore) LIMIT 100 FOR UPDATE SKIP LOCKED)
               RETURNING {Columns}
               """
            : $"""
               UPDATE TOP (100) WorkerCommands WITH (ROWLOCK, READPAST)
               SET DeliveredUtc = @now, AckedUtc = CASE WHEN JobId IS NULL THEN @now ELSE AckedUtc END
               OUTPUT inserted.CommandId, inserted.WorkerId, inserted.Kind, inserted.JobId, inserted.Reason, inserted.IssuedUtc, inserted.DeliveredUtc
               WHERE WorkerId = @workerId AND AckedUtc IS NULL AND (DeliveredUtc IS NULL OR DeliveredUtc < @redeliverBefore)
               """;
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return (await c.QueryAsync<WorkerCommandDTO>(new CommandDefinition(sql, new { workerId, now, redeliverBefore }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    /// <inheritdoc />
    public async Task AcknowledgeJobAsync(Guid jobId, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        await c.ExecuteAsync(new CommandDefinition("UPDATE WorkerCommands SET AckedUtc = @now WHERE JobId = @jobId AND AckedUtc IS NULL",
            new { jobId, now }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
    }
}
