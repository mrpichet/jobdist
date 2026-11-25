using Dapper;
using JobDist.Core.DAO;
using JobDist.Core.Domain;

namespace JobDist.Infrastructure.Data;

/// <summary><see cref="ILeaderLeaseDAO"/> for SQL Server and PostgreSQL (atomic conditional upsert).</summary>
public sealed class LeaderLeaseADO(IDbConnectionFactory factory) : ILeaderLeaseDAO
{
    private bool Pg => factory.Provider == DatabaseProvider.PostgreSql;

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(string name, string holderId, DateTimeOffset now, DateTimeOffset expiresUtc, CancellationToken ct = default)
    {
        // The row changes only if the lease is free, expired or already ours; exactly one instance wins a race.
        // Term grows by one whenever the holder changes: a fencing token and the de-duplication key of the LeaderChanged alert.
        var sql = Pg
            ? """
              INSERT INTO LeaderLeases (Name, HolderId, ExpiresUtc, Term) VALUES (@name, @holderId, @expiresUtc, 1)
              ON CONFLICT (Name) DO UPDATE SET HolderId = EXCLUDED.HolderId, ExpiresUtc = EXCLUDED.ExpiresUtc,
                  Term = CASE WHEN LeaderLeases.HolderId = @holderId THEN LeaderLeases.Term ELSE LeaderLeases.Term + 1 END
              WHERE LeaderLeases.HolderId = @holderId OR LeaderLeases.ExpiresUtc < @now
              """
            : """
              MERGE LeaderLeases WITH (HOLDLOCK) AS t
              USING (SELECT @name AS Name) AS s ON t.Name = s.Name
              WHEN MATCHED AND (t.HolderId = @holderId OR t.ExpiresUtc < @now) THEN UPDATE SET HolderId = @holderId, ExpiresUtc = @expiresUtc,
                  Term = CASE WHEN t.HolderId = @holderId THEN t.Term ELSE t.Term + 1 END
              WHEN NOT MATCHED THEN INSERT (Name, HolderId, ExpiresUtc, Term) VALUES (@name, @holderId, @expiresUtc, 1);
              """;
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        return await c.ExecuteAsync(new CommandDefinition(sql, new { name, holderId, now, expiresUtc }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async Task<LeaderLeaseDTO?> GetAsync(string name, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        var row = await c.QuerySingleOrDefaultAsync<LeaseRow>(new CommandDefinition("SELECT Name, HolderId, ExpiresUtc, Term FROM LeaderLeases WHERE Name = @name",
            new { name }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
        return row is null ? null : new LeaderLeaseDTO(row.Name, row.HolderId, row.ExpiresUtc, row.Term);
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string name, string holderId, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(ct).ConfigureAwait(false);
        // Expire instead of delete so the term keeps growing (fencing token / alert de-duplication).
        await c.ExecuteAsync(new CommandDefinition("UPDATE LeaderLeases SET ExpiresUtc = @past WHERE Name = @name AND HolderId = @holderId",
            new { name, holderId, past = DateTimeOffset.UnixEpoch }, commandTimeout: factory.CommandTimeout, cancellationToken: ct)).ConfigureAwait(false);
    }

    private sealed class LeaseRow
    {
        public string Name { get; set; } = string.Empty;
        public string HolderId { get; set; } = string.Empty;
        public DateTimeOffset ExpiresUtc { get; set; }
        public long Term { get; set; }
    }
}
