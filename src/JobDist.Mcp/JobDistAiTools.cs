using System.ComponentModel;
using System.Reflection;
using JobDist.Contracts;
using JobDist.Core.Domain;
using JobDist.Core.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace JobDist.Mcp;

/// <summary>MCP / AI tool settings (section <c>Mcp</c>).</summary>
public sealed class McpToolOptions
{
    public const string SectionName = "Mcp";

    /// <summary>
    /// Allow tools that change state (submit / cancel / re-prioritise jobs). Off by default: an AI agent should read and calculate
    /// unless an operator opts in (least privilege, limits the blast radius of prompt injection).
    /// </summary>
    public bool AllowSideEffects { get; set; }

    /// <summary>Client name recorded as the submitter of jobs created by AI agents.</summary>
    public string AgentClientName { get; set; } = "mcp-agent";
}

/// <summary>
/// Business functions exposed to AI models. Every public method with a <see cref="DescriptionAttribute"/> becomes an
/// <see cref="AIFunction"/> through <see cref="AIFunctionFactory"/>, which derives the JSON schema of parameters and result from
/// the C# signature. The same AIFunctions are used:
/// <list type="bullet">
/// <item>in-process by any <see cref="IChatClient"/> through <see cref="ChatOptions.Tools"/> (function calling), and</item>
/// <item>remotely as MCP tools via <see cref="McpServerTool.Create(AIFunction, McpServerToolCreateOptions?)"/>.</item>
/// </list>
/// Tools call the same services as the REST API, so validation, security and metrics are identical.
/// </summary>
public sealed class JobDistAiTools(
    JobRiskService risk,
    AnomalyDetectionService anomalies,
    SchedulerService scheduler,
    JobService jobs,
    WorkerRegistryService workers,
    ClusterService cluster,
    QueueThresholdService queues,
    AlertRecipientService alerts,
    JobReportService reports,
    IOptions<McpToolOptions> options)
{
    // ----------------------------------------------------------------------------------------------- risk calculation

    /// <summary>Calculates the risk that an existing job fails, is delayed or is lost.</summary>
    /// <param name="jobId">Job id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Score 0-100, level, explained factors and a recommendation.</returns>
    [Description("Calculate the risk (0-100) that a job fails, is delayed or is lost: no capable/online worker, strict target offline, backlog vs capacity, historical failure rate, expected duration vs timeout, attempts already used, unstable assigned worker. Returns score, level, factors and a recommendation.")]
    public Task<RiskAssessmentDTO> AssessJobRisk(
        [Description("Job id (GUID).")] Guid jobId,
        CancellationToken ct = default) =>
        risk.AssessJobAsync(jobId, ct);

    /// <summary>What-if risk before submitting a job.</summary>
    [Description("What-if: calculate the risk (0-100) of submitting a job of the given type BEFORE creating it (capability, redundancy, backlog, history, timeout, encryption keys for sensitive payloads). Nothing is created.")]
    public Task<RiskAssessmentDTO> AssessSubmissionRisk(
        [Description("Job type, e.g. 'image.resize'.")] string jobType,
        [Description("Optional worker id (GUID) that should run the job.")] Guid? targetWorkerId = null,
        [Description("true = run only on targetWorkerId.")] bool strictTarget = false,
        [Description("Timeout per attempt in seconds (null = server default).")] int? timeoutSeconds = null,
        [Description("true when the payload is confidential (needs encrypted delivery).")] bool sensitive = false,
        CancellationToken ct = default) =>
        risk.AssessSubmissionAsync(jobType, targetWorkerId, strictTarget, timeoutSeconds, sensitive, ct);

    /// <summary>Health risk of a worker.</summary>
    [Description("Calculate the health risk (0-100) of a worker: availability, network instability (offline count), learned failure rate per job type, CPU/memory pressure and missing post-quantum keys.")]
    public Task<RiskAssessmentDTO> AssessWorkerRisk(
        [Description("Worker id (GUID).")] Guid workerId,
        CancellationToken ct = default) =>
        risk.AssessWorkerAsync(workerId, ct);

    /// <summary>Scans recent activity for anomalies.</summary>
    [Description("Detect abnormal job distribution in a recent window: submission spikes (z-score), high failure rates per job type or worker, slow execution, flapping workers, fail-over storms, queues without capable workers and suspicious worker reports.")]
    public Task<AnomalyReportDTO> DetectAnomalies(
        [Description("Window size in minutes (5-1440).")] int windowMinutes = 15,
        CancellationToken ct = default) =>
        anomalies.ScanAsync(windowMinutes, ct);

    /// <summary>Ranks workers for a job type.</summary>
    [Description("Rank online workers for a job type with the scheduler's own scoring (load, ML-predicted speed and reliability, exploration). Explains why each worker is ranked.")]
    public Task<IReadOnlyList<WorkerRecommendationDTO>> RecommendWorkers(
        [Description("Job type.")] string jobType,
        [Description("Max results (1-100).")] int top = 5,
        CancellationToken ct = default) =>
        scheduler.RecommendAsync(jobType, top, ct);

    // ----------------------------------------------------------------------------------------------- read-only status

    /// <summary>Gets a job's status.</summary>
    [Description("Get the status of a job: state, priority, assigned worker, attempts, timings, result or last error.")]
    public Task<JobStatusResponse> GetJobStatus([Description("Job id (GUID).")] Guid jobId, CancellationToken ct = default) =>
        jobs.GetAsync(jobId, ct);

    /// <summary>Lists jobs.</summary>
    [Description("List recent jobs, optionally filtered by status (Pending, Assigned, Running, Succeeded, Failed, CancelRequested, Cancelled) and job type.")]
    public Task<PagedResponse<JobStatusResponse>> ListJobs(
        [Description("Optional status filter.")] JobStatus? status = null,
        [Description("Optional job type filter.")] string? jobType = null,
        [Description("Max results (1-200).")] int take = 50,
        CancellationToken ct = default) =>
        jobs.QueryAsync(new JobQueryDTO(status, jobType, null, 0, Math.Clamp(take, 1, 200)), ct);

    /// <summary>Distribution history of a job.</summary>
    [Description("Get the distribution history of a job: every attempt with worker, queue wait, duration, outcome and scheduler score.")]
    public Task<IReadOnlyList<JobAttemptResponse>> GetJobHistory([Description("Job id (GUID).")] Guid jobId, CancellationToken ct = default) =>
        jobs.GetAttemptsAsync(jobId, ct);

    /// <summary>Lists workers.</summary>
    [Description("List registered workers with status, capabilities (job types and concurrency), machine spec, current load and stability.")]
    public Task<IReadOnlyList<WorkerResponse>> ListWorkers(CancellationToken ct = default) =>
        workers.ListAsync(false, ct);

    /// <summary>Cluster overview.</summary>
    [Description("Get a cluster overview: workers online/draining/offline, capacity, jobs by status, pending jobs per type and the current HA leader.")]
    public Task<ClusterSummaryDTO> GetClusterSummary(CancellationToken ct = default) =>
        cluster.GetSummaryAsync(ct);

    /// <summary>Learned performance statistics.</summary>
    [Description("Get what the ML model learned about worker performance per job type: samples, success/failure counts, mean and standard deviation of duration.")]
    public IReadOnlyList<PerformanceStatDTO> GetWorkerPerformance([Description("Optional job type filter.")] string? jobType = null) =>
        cluster.GetPerformance(jobType);

    // ----------------------------------------------------------------------------------------------- console (read-only)

    /// <summary>Current queue usage against every configured threshold.</summary>
    [Description("Get pending jobs compared with the configured queue thresholds: pending, limit, usage percent and level (Normal, NearLimit, OverLimit). Over the limit, new jobs of that type are rejected.")]
    public Task<IReadOnlyList<QueueStatusResponse>> GetQueueStatus(CancellationToken ct = default) => queues.GetStatusAsync(ct);

    /// <summary>Recent alert deliveries.</summary>
    [Description("List alerts sent to administrators through MessageHub in the last hours: event type, severity, title, recipient (masked address) and delivery status.")]
    public async Task<IReadOnlyList<AlertLogResponse>> ListAlertLogs(
        [Description("Look back this many hours (1-720).")] int hours = 24,
        [Description("Optional event type: AnomalyDetected, WorkerRegistered, WorkerUnregistered, WorkerLost, LeaderChanged, QueueNearLimit, QueueOverLimit, Test.")] AlertEventType? eventType = null,
        CancellationToken ct = default)
    {
        var to = DateTimeOffset.UtcNow;
        var page = await alerts.QueryLogsAsync(to.AddHours(-Math.Clamp(hours, 1, 720)), to, eventType, null, null, 0, 200, ct).ConfigureAwait(false);
        return page.Items;
    }

    /// <summary>Job statistics report.</summary>
    [Description("Job statistics per day (Asia/Bangkok), job type or worker: submitted, succeeded, failed, cancelled, success rate, average attempts, average and P95 duration, average queue wait, throughput per hour. Dates are yyyy-MM-dd; range at most the retention period (30 days).")]
    public Task<JobStatisticsReportResponse> GetJobStatistics(
        [Description("First day, yyyy-MM-dd.")] string fromDate,
        [Description("Last day, yyyy-MM-dd.")] string toDate,
        [Description("Optional job type filter.")] string? jobType = null,
        [Description("Grouping: day, jobType, worker or a comma-separated combination.")] string groupBy = "day",
        CancellationToken ct = default)
    {
        if (!DateOnly.TryParseExact(fromDate, "yyyy-MM-dd", out var from) || !DateOnly.TryParseExact(toDate, "yyyy-MM-dd", out var to))
        {
            throw new ValidationException("Dates must be yyyy-MM-dd.");
        }
        return reports.GetAsync(from, to, jobType, null, groupBy, "Asia/Bangkok", ct);
    }

    // ----------------------------------------------------------------------------------------------- side effects (opt-in)

    /// <summary>Submits a job (only when side effects are enabled).</summary>
    [Description("Submit a new job. Only available when the operator enabled side-effect tools. Use assess_submission_risk first.")]
    public Task<SubmitJobResponse> SubmitJob(
        [Description("Job type.")] string jobType,
        [Description("Job payload (JSON text).")] string? payload = null,
        [Description("Priority 0-100 (higher runs first).")] int priority = 50,
        [Description("Optional preferred/strict worker id.")] Guid? targetWorkerId = null,
        [Description("true = only on targetWorkerId.")] bool strictTarget = false,
        [Description("Idempotency key so retries do not create duplicates.")] string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        // Step 1: least-privilege switch. Step 2: same service as POST /api/jobs.
        EnsureSideEffectsAllowed();
        return jobs.SubmitAsync(new SubmitJobRequest(jobType, payload, priority, targetWorkerId, strictTarget, IdempotencyKey: idempotencyKey),
            options.Value.AgentClientName, ct);
    }

    /// <summary>Cancels a job (only when side effects are enabled).</summary>
    [Description("Cancel a job. If a worker is running it, the server sends the worker a signed cancel command. Only available when side-effect tools are enabled.")]
    public Task<JobStatusResponse> CancelJob(
        [Description("Job id (GUID).")] Guid jobId,
        [Description("Reason recorded on the job.")] string? reason = null,
        CancellationToken ct = default)
    {
        EnsureSideEffectsAllowed();
        return jobs.CancelAsync(jobId, reason ?? "Cancelled by AI agent", ct);
    }

    /// <summary>Changes a job's priority (only when side effects are enabled).</summary>
    [Description("Change the priority (0-100) of a job that has not finished. Only available when side-effect tools are enabled.")]
    public Task<JobStatusResponse> ChangeJobPriority(
        [Description("Job id (GUID).")] Guid jobId,
        [Description("New priority 0-100.")] int priority,
        CancellationToken ct = default)
    {
        EnsureSideEffectsAllowed();
        return jobs.ChangePriorityAsync(jobId, priority, ct);
    }

    private void EnsureSideEffectsAllowed()
    {
        if (!options.Value.AllowSideEffects)
        {
            throw new InvalidOperationException("Side-effect tools are disabled (Mcp:AllowSideEffects=false).");
        }
    }
}

/// <summary>Factory / registration helpers for the AI tools.</summary>
public static class JobDistAiFunctions
{
    /// <summary>
    /// Creates <see cref="AIFunction"/> definitions for every tool method. The target instance is resolved per call from
    /// <see cref="AIFunctionArguments.Services"/> (provided by the MCP server or by <c>FunctionInvokingChatClient</c>),
    /// so the definitions are static and created once.
    /// </summary>
    /// <param name="includeSideEffects">Include state-changing tools (they still check <c>Mcp:AllowSideEffects</c> at call time).</param>
    /// <returns>AI functions with snake_case names.</returns>
    public static IReadOnlyList<AIFunction> Create(bool includeSideEffects = true)
    {
        // Step 1: discover annotated public instance methods.
        var sideEffects = new HashSet<string>(StringComparer.Ordinal) { nameof(JobDistAiTools.SubmitJob), nameof(JobDistAiTools.CancelJob), nameof(JobDistAiTools.ChangeJobPriority) };
        var methods = typeof(JobDistAiTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<DescriptionAttribute>() is not null)
            .Where(m => includeSideEffects || !sideEffects.Contains(m.Name));

        // Step 2: build AIFunctions whose target instance comes from DI at invocation time.
        return methods.Select(m => (AIFunction)AIFunctionFactory.Create(
                m,
                args => (args.Services ?? throw new InvalidOperationException("AIFunctionArguments.Services is required."))
                    .GetRequiredService<JobDistAiTools>(),
                new AIFunctionFactoryOptions { Name = ToSnakeCase(m.Name) }))
            .ToList();
    }

    /// <summary>Registers the tool class and its options.</summary>
    public static IServiceCollection AddJobDistAiTools(this IServiceCollection services)
    {
        services.AddOptions<McpToolOptions>().BindConfiguration(McpToolOptions.SectionName);
        services.AddSingleton<JobDistAiTools>();
        return services;
    }

    /// <summary>
    /// Adds every JobDist AI function to an MCP server as a tool. Side-effect tools are only listed when enabled.
    /// </summary>
    /// <param name="builder">MCP server builder.</param>
    /// <param name="allowSideEffects">Whether state-changing tools are advertised.</param>
    public static IMcpServerBuilder WithJobDistTools(this IMcpServerBuilder builder, bool allowSideEffects) =>
        builder.WithTools(Create(allowSideEffects).Select(f => McpServerTool.Create(f)));

    internal static string ToSnakeCase(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
