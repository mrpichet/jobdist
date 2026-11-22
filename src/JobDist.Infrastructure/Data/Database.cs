using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using JobDist.Core.Domain;
using JobDist.Core.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace JobDist.Infrastructure.Data;

/// <summary>Supported database engines.</summary>
public enum DatabaseProvider
{
    /// <summary>Microsoft SQL Server / Azure SQL (Microsoft.Data.SqlClient).</summary>
    SqlServer = 0,
    /// <summary>PostgreSQL 14+ (Npgsql).</summary>
    PostgreSql = 1,
    /// <summary>Process memory (development, demos, unit tests). Not HA - single instance only.</summary>
    InMemory = 2
}

/// <summary>Database settings (section <c>Database</c>).</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Engine.</summary>
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.SqlServer;

    /// <summary>Connection string (supply via environment / secret store; use TLS in production).</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Create the database if missing and apply the embedded idempotent schema at start-up.</summary>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>Command timeout in seconds.</summary>
    [Range(1, 600)] public int CommandTimeoutSeconds { get; set; } = 30;
}

/// <summary>Opens pooled connections for the configured provider.</summary>
public interface IDbConnectionFactory
{
    /// <summary>Configured provider.</summary>
    DatabaseProvider Provider { get; }

    /// <summary>Command timeout for Dapper calls.</summary>
    int CommandTimeout { get; }

    /// <summary>Opens a new pooled connection; the caller disposes it.</summary>
    Task<DbConnection> OpenAsync(CancellationToken ct = default);
}

/// <summary>Default connection factory (SqlClient / Npgsql handle pooling).</summary>
public sealed class DbConnectionFactory : IDbConnectionFactory
{
    private readonly DatabaseOptions _options;
    private readonly NpgsqlDataSource? _npgsql;

    /// <summary>Creates the factory; a shared <see cref="NpgsqlDataSource"/> is built once for PostgreSQL.</summary>
    public DbConnectionFactory(IOptions<DatabaseOptions> options)
    {
        _options = options.Value;
        if (_options.Provider == DatabaseProvider.PostgreSql)
        {
            _npgsql = NpgsqlDataSource.Create(_options.ConnectionString);
        }
        DapperSetup.EnsureRegistered();
    }

    /// <inheritdoc />
    public DatabaseProvider Provider => _options.Provider;

    /// <inheritdoc />
    public int CommandTimeout => _options.CommandTimeoutSeconds;

    /// <inheritdoc />
    public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
    {
        // Step 1: create for the provider; Step 2: open asynchronously.
        DbConnection connection = _options.Provider switch
        {
            DatabaseProvider.SqlServer => new SqlConnection(_options.ConnectionString),
            DatabaseProvider.PostgreSql => _npgsql!.CreateConnection(),
            _ => throw new InvalidOperationException("The in-memory provider has no connections.")
        };
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }
}

/// <summary>
/// Dapper type handlers shared by both providers:
/// <list type="bullet">
/// <item>DateTimeOffset - always written as UTC; PostgreSQL <c>timestamptz</c> values (read as DateTime UTC) are converted back.</item>
/// <item>JSON columns - lists / small objects (tags, capabilities, spec) stored as JSON text.</item>
/// </list>
/// </summary>
internal static class DapperSetup
{
    private static int _registered;

    /// <summary>Registers handlers once per process.</summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
        SqlMapper.AddTypeHandler(new JsonHandler<List<string>>());
        SqlMapper.AddTypeHandler(new JsonHandler<List<WorkerCapabilityDTO>>());
        SqlMapper.AddTypeHandler(new JsonHandler<WorkerSpecDTO>());
    }

    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            parameter.DbType = DbType.DateTimeOffset;
            parameter.Value = value.ToUniversalTime();
        }

        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => throw new DataException($"Cannot convert {value.GetType()} to DateTimeOffset.")
        };
    }

    private sealed class JsonHandler<T> : SqlMapper.TypeHandler<T>
    {
        public override void SetValue(IDbDataParameter parameter, T? value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = JsonSerializer.Serialize(value);
        }

        public override T? Parse(object value) => value is string s && s.Length > 0 ? JsonSerializer.Deserialize<T>(s) : default;
    }
}

/// <summary>Maps provider-specific errors to business exceptions.</summary>
internal static class DbErrors
{
    /// <summary><c>true</c> for unique-key violations (SQL Server 2601/2627, PostgreSQL 23505).</summary>
    public static bool IsUniqueViolation(Exception ex) =>
        ex is SqlException { Number: 2601 or 2627 } || ex is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>Runs an insert and converts a unique-key violation into <see cref="ConflictException"/>.</summary>
    public static async Task WrapUniqueAsync(Func<Task> action, string message)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (DbException ex) when (IsUniqueViolation(ex))
        {
            throw new ConflictException(message);
        }
    }
}

/// <summary>Creates the database if needed and applies the embedded schema of the configured provider.</summary>
public sealed partial class DatabaseInitializer(IDbConnectionFactory factory, IOptions<DatabaseOptions> options, ILogger<DatabaseInitializer> logger)
{
    /// <summary>
    /// Applies the idempotent schema with retries while the database container starts.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task InitializeAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!o.AutoMigrate || o.Provider == DatabaseProvider.InMemory)
        {
            return;
        }

        // Step 1: read the embedded script; SQL Server batches are split on GO lines.
        var resource = o.Provider == DatabaseProvider.SqlServer ? "JobDist.sqlserver.schema.sql" : "JobDist.postgres.schema.sql";
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded {resource} not found.");
        using var reader = new StreamReader(stream);
        var script = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        var batches = o.Provider == DatabaseProvider.SqlServer
            ? GoSeparator().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)).ToList()
            : [script];

        // Step 2: execute with retry (database may still be starting).
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await EnsureDatabaseExistsAsync(o, ct).ConfigureAwait(false);
                await using var connection = await factory.OpenAsync(ct).ConfigureAwait(false);
                foreach (var batch in batches)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = batch;
                    command.CommandTimeout = 120;
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                logger.LogInformation("{Provider} schema verified ({Count} batches).", o.Provider, batches.Count);
                return;
            }
            catch (DbException ex) when (attempt < 30 && !ct.IsCancellationRequested)
            {
                logger.LogWarning("Database not ready (attempt {Attempt}): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, attempt * 2)), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Creates the target database through the server's maintenance database when it does not exist.</summary>
    private static async Task EnsureDatabaseExistsAsync(DatabaseOptions o, CancellationToken ct)
    {
        if (o.Provider == DatabaseProvider.SqlServer)
        {
            // Name passed as a parameter and quoted with QUOTENAME → no SQL injection through configuration.
            var csb = new SqlConnectionStringBuilder(o.ConnectionString);
            var database = csb.InitialCatalog;
            if (string.IsNullOrWhiteSpace(database) || database.Equals("master", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            csb.InitialCatalog = "master";
            await using var master = new SqlConnection(csb.ConnectionString);
            await master.OpenAsync(ct).ConfigureAwait(false);
            await master.ExecuteAsync(new CommandDefinition(
                "IF DB_ID(@name) IS NULL BEGIN DECLARE @sql NVARCHAR(300) = N'CREATE DATABASE ' + QUOTENAME(@name); EXEC (@sql); END",
                new { name = database }, cancellationToken: ct)).ConfigureAwait(false);
        }
        else
        {
            // PostgreSQL cannot parameterise identifiers: only a strict allow-list of characters is accepted.
            var csb = new NpgsqlConnectionStringBuilder(o.ConnectionString);
            var database = csb.Database;
            if (string.IsNullOrWhiteSpace(database) || database == "postgres")
            {
                return;
            }
            if (!SafeIdentifier().IsMatch(database))
            {
                throw new InvalidOperationException("Database name may contain only letters, digits and '_'.");
            }
            csb.Database = "postgres";
            await using var admin = new NpgsqlConnection(csb.ConnectionString);
            await admin.OpenAsync(ct).ConfigureAwait(false);
            var exists = await admin.ExecuteScalarAsync<int?>(new CommandDefinition("SELECT 1 FROM pg_database WHERE datname = @name", new { name = database }, cancellationToken: ct)).ConfigureAwait(false);
            if (exists is null)
            {
                await admin.ExecuteAsync(new CommandDefinition($"CREATE DATABASE \"{database}\"", cancellationToken: ct)).ConfigureAwait(false);
            }
        }
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$")]
    private static partial Regex SafeIdentifier();
}
