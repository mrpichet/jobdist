using Dapper;
using JobDist.Contracts;
using JobDist.Core.DAO;
using JobDist.Core.Domain;

namespace JobDist.Infrastructure.Data;

/// <summary><see cref="IWorkerDAO"/> for SQL Server and PostgreSQL. Capabilities, spec and tags are JSON columns.</summary>
public sealed class WorkerADO(IDbConnectionFactory factory) : IWorkerDAO
{
    private bool Pg => factory.Provider == DatabaseProvider.PostgreSql;

    private const string Columns =
        "WorkerId, Name, Status, Capabilities, Spec, MaxTotalConcurrency, TokenHash, EcdhPublicKey, MlKemPublicKey, Version, Tags, RegisteredUtc, " +
        "LastHeartbeatUtc, CpuUsagePercent, MemoryUsagePercent, OfflineCount, RegisteredBy";

    /// <inheritdoc />
    public async Task UpsertAsync(WorkerDTO worker, CancellationToken ct = default)
    {
        // Atomic upsert keyed by id (MERGE with HOLDLOCK on SQL Server, ON CONFLICT on PostgreSQL).
        var sql = Pg
            ? $"""
               INSERT INTO Workers ({Columns}) VALUES (@WorkerId, @Name, @Status, @Capabilities, @Spec, @MaxTotalConcurrency, @TokenHash, @EcdhPublicKey,
                   @MlKemPublicKey, @Version, @Tags, @RegisteredUtc, @LastHeartbeatUtc, @CpuUsagePercent, @MemoryUsagePercent, @OfflineCount, @RegisteredBy)
               ON CONFLICT (WorkerId) DO UPDATE SET Name = EXCLUDED.Name, Status = EXCLUDED.Status, Capabilities = EXCLUDED.Capabilities, Spec = EXCLUDED.Spec,
                   MaxTotalConcurrency = EXCLUDED.MaxTotalConcurrency, TokenHash = EXCLUDED.TokenHash, EcdhPublicKey = EXCLUDED.EcdhPublicKey,
                   MlKemPublicKey = EXCLUDED.MlKemPublicKey, Version = EXCLUDED.Version, Tags = EXCLUDED.Tags, LastHeartbeatUtc = EXCLUDED.LastHeartbeatUtc,
                   OfflineCount = EXCLUDED.OfflineCount, RegisteredBy = EXCLUDED.RegisteredBy
               """
            : $"""
               MERGE Workers WITH (HOLDLOCK) AS t
               USING (SELECT @WorkerId AS WorkerId) AS s ON t.WorkerId = s.WorkerId
               WHEN MATCHED THEN UPDATE SET Name = @Name, Status = @Status, Capabilities = @Capabilities, Spec = @Spec, MaxTotalConcurrency = @MaxTotalConcurrency,
                   TokenHash = @TokenHash, EcdhPublicKey = @EcdhPublicKey, MlKemPublicKey = @MlKemPublicKey, Version = @Version, Tags = @Tags,
                   LastHeartbeatUtc = @LastHeartbeatUtc, OfflineCount = @OfflineCount, RegisteredBy = @RegisteredBy
               WHEN NOT MATCHED THEN INSERT ({Columns}) VALUES (@WorkerId, @Name, @Status, @Capabilities, @Spec, @MaxTotalConcurrency, @TokenHash, @EcdhPublicKey,
                   @MlKemPublicKey, @Version, @Tags, @RegisteredUtc, @LastHeartbeatUtc, @CpuUsagePercent, @MemoryUsagePercent, @OfflineCount, @RegisteredBy);
               """;
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        await DbErrors.WrapUniqueAsync(() => c.ExecuteAsync(new CommandDefinition(sql, new
        {
            worker.WorkerId, worker.Name, Status = (int)worker.Status, worker.Capabilities, worker.Spec, worker.MaxTotalConcurrency, worker.TokenHash,
            worker.EcdhPublicKey, worker.MlKemPublicKey, worker.Version, worker.Tags, worker.RegisteredUtc, worker.LastHeartbeatUtc,
            worker.CpuUsagePercent, worker.MemoryUsagePercent, worker.OfflineCount, worker.RegisteredBy
        }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)), "A worker with this name already exists.").ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WorkerDTO?> GetAsync(Guid workerId, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<WorkerDTO>(new CommandDefinition($"SELECT {Columns} FROM Workers WHERE WorkerId = @workerId", new { workerId },
            commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WorkerDTO?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return await c.QuerySingleOrDefaultAsync<WorkerDTO>(new CommandDefinition($"SELECT {Columns} FROM Workers WHERE Name = @name", new { name },
            commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkerDTO>> ListAsync(bool includeUnregistered, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return (await c.QueryAsync<WorkerDTO>(new CommandDefinition($"SELECT {Columns} FROM Workers WHERE @all = 1 OR Status <> 3 ORDER BY Name",
            new { all = includeUnregistered ? 1 : 0 }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    /// <inheritdoc />
    public async Task<WorkerStatus?> RecordHeartbeatAsync(Guid workerId, DateTimeOffset now, double cpu, double memory, CancellationToken ct = default)
    {
        // Offline (2) → Online (0) when the worker comes back; Draining stays Draining; Unregistered (3) is never revived.
        var sql = Pg
            ? """
              UPDATE Workers SET LastHeartbeatUtc = @now, CpuUsagePercent = @cpu, MemoryUsagePercent = @memory,
                     Status = CASE WHEN Status = 2 THEN 0 ELSE Status END
              WHERE WorkerId = @workerId AND Status <> 3
              RETURNING Status
              """
            : """
              UPDATE Workers SET LastHeartbeatUtc = @now, CpuUsagePercent = @cpu, MemoryUsagePercent = @memory,
                     Status = CASE WHEN Status = 2 THEN 0 ELSE Status END
              OUTPUT inserted.Status
              WHERE WorkerId = @workerId AND Status <> 3
              """;
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        var status = await c.ExecuteScalarAsync<int?>(new CommandDefinition(sql, new { workerId, now, cpu, memory }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
        return status is null ? null : (WorkerStatus)status.Value;
    }

    /// <inheritdoc />
    public async Task<bool> SetStatusAsync(Guid workerId, WorkerStatus status, bool incrementOfflineCount, WorkerStatus? onlyIfStatus, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE Workers SET Status = @status, OfflineCount = OfflineCount + @inc
            WHERE WorkerId = @workerId AND (@only IS NULL OR Status = @only)
            """;
        var p = new DynamicParameters();
        p.Add("status", (int)status);
        p.Add("inc", incrementOfflineCount ? 1 : 0);
        p.Add("workerId", workerId);
        p.Add("only", onlyIfStatus is null ? null : (int?)onlyIfStatus, System.Data.DbType.Int32);
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return await c.ExecuteAsync(new CommandDefinition(sql, p, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkerDTO>> GetStaleAsync(DateTimeOffset heartbeatBefore, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return (await c.QueryAsync<WorkerDTO>(new CommandDefinition($"SELECT {Columns} FROM Workers WHERE Status IN (0, 1) AND LastHeartbeatUtc < @heartbeatBefore",
            new { heartbeatBefore }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }
}
