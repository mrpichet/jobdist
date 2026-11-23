using JobDist.Contracts;
using JobDist.Contracts.Security;
using JobDist.Core.Domain;
using JobDist.Core.Services;
using JobDist.Infrastructure;
using JobDist.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace JobDist.Tests;

/// <summary>
/// Builds the real service graph (same registrations as the server) on the in-memory DAO with a fake clock,
/// so tests exercise the actual business services end to end without a database.
/// </summary>
internal sealed class TestHost : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _provider;

    public TestHost(Dictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "InMemory",
            ["JobDist:InstanceId"] = "test-1",
            ["JobDist:RetryBaseDelaySeconds"] = "2",
            ["JobDist:RetryMaxDelaySeconds"] = "60",
            ["JobDist:OfflineAfterSeconds"] = "20",
            ["JobDist:AssignmentStartTimeoutSeconds"] = "60",
            ["JobDist:AgingPointsPerMinute"] = "0",
            ["Mcp:AllowSideEffects"] = "false",
        };
        foreach (var (k, v) in overrides ?? [])
        {
            settings[k] = v;
        }
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        Time = new FakeTimeProvider(Start);
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton(Configuration);
        services.AddJobDist(Configuration, runBackgroundWorkers: false);
        services.AddJobDistAiTools();
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public IConfiguration Configuration { get; }

    public FakeTimeProvider Time { get; }

    public IServiceProvider Services => _provider;

    public T Get<T>() where T : notnull => _provider.GetRequiredService<T>();

    public JobService Jobs => Get<JobService>();

    public WorkerRegistryService Registry => Get<WorkerRegistryService>();

    public SchedulerService Scheduler => Get<SchedulerService>();

    public DispatchService Dispatch => Get<DispatchService>();

    public JobExecutionService Execution => Get<JobExecutionService>();

    public FailoverService Failover => Get<FailoverService>();

    /// <summary>Registers a worker with the given job types (concurrency per type) and optional encryption keys.</summary>
    public async Task<RegisterWorkerResponse> RegisterAsync(string name, int maxTotal = 4, double benchmark = 1.0, HybridKeyPair? keys = null, params (string Type, int Max)[] caps)
    {
        var capabilities = (caps.Length == 0 ? [("demo", 4)] : caps).Select(c => new WorkerCapabilityRequest(c.Type, c.Max)).ToList();
        return await Registry.RegisterAsync(new RegisterWorkerRequest(name, capabilities, new WorkerSpecRequest(8, 16_384, 0, benchmark),
            maxTotal, keys?.EcdhPublicKeyBase64, keys?.MlKemPublicKeyBase64, "test"), "test", CancellationToken.None);
    }

    /// <summary>Submits a job with sensible defaults.</summary>
    public Task<SubmitJobResponse> SubmitAsync(string type = "demo", int priority = 50, string? payload = "{}", Guid? target = null, bool strict = false,
        int? maxAttempts = null, bool sensitive = false, string? idempotencyKey = null) =>
        Jobs.SubmitAsync(new SubmitJobRequest(type, payload, priority, target, strict, maxAttempts, 60, null, idempotencyKey, sensitive), "tester", CancellationToken.None);

    /// <summary>Polls without waiting.</summary>
    public Task<PollResponse> PollAsync(Guid workerId, int max = 8) => Dispatch.PollAsync(workerId, new PollRequest(max, 0), CancellationToken.None);

    /// <summary>Runs one scheduler cycle.</summary>
    public Task<int> ScheduleAsync() => Scheduler.RunCycleAsync(CancellationToken.None);

    /// <summary>Completes an attempt.</summary>
    public Task<JobStatusResponse> CompleteAsync(JobAssignmentResponse a, AttemptOutcome outcome, double ms = 100, bool retryable = true, string? result = null) =>
        Execution.CompletedAsync(a.WorkerId, a.JobId, new JobCompletedRequest(a.AttemptId, outcome, ms, result, outcome == AttemptOutcome.Succeeded ? null : "boom", retryable), CancellationToken.None);

    /// <summary>Assign → poll → start: returns the assignment of the given job for the given worker.</summary>
    public async Task<JobAssignmentResponse> DeliverAsync(Guid workerId, Guid jobId)
    {
        await ScheduleAsync();
        var poll = await PollAsync(workerId);
        var a = poll.Assignments.Single(x => x.JobId == jobId);
        await Execution.StartedAsync(workerId, jobId, a.AttemptId, CancellationToken.None);
        return a;
    }

    public void Dispose() => _provider.Dispose();
}
