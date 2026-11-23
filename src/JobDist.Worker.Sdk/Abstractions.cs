using System.ComponentModel.DataAnnotations;

namespace JobDist.Worker.Sdk;

/// <summary>
/// Implemented by applications: runs one job type. One handler instance is resolved per execution from a DI scope,
/// so handlers may depend on scoped services (DbContext, HttpClient, ...).
/// </summary>
public interface IJobHandler
{
    /// <summary>Job type this handler runs (e.g. <c>image.resize</c>). Must be unique per worker.</summary>
    string JobType { get; }

    /// <summary>Maximum parallel executions of this type on this worker (advertised to the server).</summary>
    int MaxConcurrency => 1;

    /// <summary>Self-declared relative speed (1.0 = baseline). The server's ML model learns the real value from history.</summary>
    double SpeedFactor => 1.0;

    /// <summary>
    /// Executes one job.
    /// </summary>
    /// <param name="context">Payload and metadata of the job.</param>
    /// <param name="ct">Cancelled when the job times out, is cancelled by the server, or the worker shuts down.</param>
    /// <returns>Result. Throwing an exception is reported as a retryable failure; throw <see cref="NonRetryableJobException"/> to stop retries.</returns>
    Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct);
}

/// <summary>Information about the job being executed.</summary>
/// <param name="JobId">Job id.</param>
/// <param name="AttemptId">Attempt id (unique per distribution).</param>
/// <param name="JobType">Job type.</param>
/// <param name="Attempt">1-based attempt number (greater than 1 means this is a retry: make the handler idempotent).</param>
/// <param name="Priority">Priority 0-100.</param>
/// <param name="Timeout">Execution timeout given by the server.</param>
/// <param name="Payload">Decrypted payload (usually JSON), or null.</param>
/// <param name="WasEncrypted">True when the payload arrived as a post-quantum hybrid envelope.</param>
public sealed record JobContext(
    Guid JobId,
    Guid AttemptId,
    string JobType,
    int Attempt,
    int Priority,
    TimeSpan Timeout,
    string? Payload,
    bool WasEncrypted);

/// <summary>Outcome of a handler.</summary>
/// <param name="Success">True when the job succeeded.</param>
/// <param name="Output">Optional output text / JSON (max 1 MB).</param>
/// <param name="Error">Error text on failure (max 4,000 chars; never include secrets).</param>
/// <param name="Retryable">Whether a failure may be retried on another attempt.</param>
public sealed record JobResult(bool Success, string? Output = null, string? Error = null, bool Retryable = true)
{
    /// <summary>Creates a successful result.</summary>
    /// <param name="output">Optional output.</param>
    /// <returns>Result.</returns>
    public static JobResult Ok(string? output = null) => new(true, output);

    /// <summary>Creates a failed result.</summary>
    /// <param name="error">Error text.</param>
    /// <param name="retryable">Whether the server may retry the job.</param>
    /// <returns>Result.</returns>
    public static JobResult Fail(string error, bool retryable = true) => new(false, null, error, retryable);
}

/// <summary>Throw from a handler when retrying cannot help (bad input, permanent business rule).</summary>
/// <param name="message">Error text reported to the server.</param>
public sealed class NonRetryableJobException(string message) : Exception(message);

/// <summary>Worker settings (section <c>JobDistWorker</c>).</summary>
public sealed class JobDistWorkerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "JobDistWorker";

    /// <summary>All server instances (HA). The worker fails over between them on network errors and 5xx.</summary>
    [MinLength(1)]
    public List<string> ServerUrls { get; set; } = ["http://localhost:8080"];

    /// <summary>API key with the <c>Worker</c> role (inject through a secret store or environment variable).</summary>
    [Required]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Unique worker name. Defaults to the machine name. Re-using the name after a crash re-uses the worker id.</summary>
    [RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9._\-:]*$")]
    public string? WorkerName { get; set; }

    /// <summary>Total parallel jobs across all types. Defaults to the number of logical CPU cores.</summary>
    [Range(0, 4096)]
    public int MaxTotalConcurrency { get; set; }

    /// <summary>Per-type concurrency overrides (job type → max concurrency), take precedence over the handler default.</summary>
    public Dictionary<string, int> ConcurrencyOverrides { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Relative machine benchmark (1.0 = baseline). 0 = measure automatically at start-up.</summary>
    [Range(0, 1000)]
    public double BenchmarkScore { get; set; }

    /// <summary>Number of GPUs available to jobs.</summary>
    [Range(0, 64)]
    public int GpuCount { get; set; }

    /// <summary>Labels such as <c>zone=a</c>.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Long-poll wait in seconds (0-30).</summary>
    [Range(0, 30)]
    public int PollWaitSeconds { get; set; } = 20;

    /// <summary>Reject assignments and commands whose server signature does not verify (strongly recommended).</summary>
    public bool RequireSignatures { get; set; } = true;

    /// <summary>
    /// Optional pin of the server signing key id. When set, keys with another id are rejected and never refreshed
    /// automatically (protects against a malicious server that tries to rotate in its own key).
    /// </summary>
    public string? PinnedServerKeyId { get; set; }

    /// <summary>HTTP timeout for normal (non long-poll) calls.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a completion report is retried while the server is unreachable before it is dropped.</summary>
    public TimeSpan ReportRetention { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Unregister from the server on graceful shutdown (running jobs are re-distributed immediately).</summary>
    public bool UnregisterOnShutdown { get; set; } = true;

    /// <summary>Time given to running jobs to finish during shutdown before they are cancelled.</summary>
    public TimeSpan ShutdownGracePeriod { get; set; } = TimeSpan.FromSeconds(20);
}
