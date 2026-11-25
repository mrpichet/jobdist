using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobDist.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobDist.Client;

/// <summary>Client settings (section <c>JobDistClient</c>).</summary>
public sealed class JobDistClientOptions
{
    public const string SectionName = "JobDistClient";

    /// <summary>One or more server URLs; on network errors / 5xx the client fails over to the next (HA).</summary>
    public List<string> ServerUrls { get; set; } = ["http://localhost:8080"];

    /// <summary>API key with the Submitter role (from a secret store / environment variable).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Per-request timeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Job submission API for other projects.</summary>
public interface IJobDistClient
{
    /// <summary>Submits a job. An idempotency key is generated when missing so automatic fail-over retries never duplicate the job.</summary>
    /// <param name="request">Job definition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Job id and status.</returns>
    Task<SubmitJobResponse> SubmitAsync(SubmitJobRequest request, CancellationToken ct = default);

    /// <summary>Submits up to 5,000 jobs in one call.</summary>
    Task<IReadOnlyList<SubmitJobResponse>> SubmitBatchAsync(IReadOnlyList<SubmitJobRequest> jobs, CancellationToken ct = default);

    /// <summary>Gets a job's status.</summary>
    Task<JobStatusResponse> GetAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Lists jobs.</summary>
    Task<PagedResponse<JobStatusResponse>> ListAsync(JobStatus? status = null, string? jobType = null, int skip = 0, int take = 100, CancellationToken ct = default);

    /// <summary>Distribution history of a job.</summary>
    Task<IReadOnlyList<JobAttemptResponse>> GetAttemptsAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Changes the priority of an unfinished job (0-100).</summary>
    Task<JobStatusResponse> ChangePriorityAsync(Guid jobId, int priority, CancellationToken ct = default);

    /// <summary>Cancels a job (the server tells the worker to stop).</summary>
    Task<JobStatusResponse> CancelAsync(Guid jobId, string? reason = null, CancellationToken ct = default);

    /// <summary>Polls until the job reaches a final state (Succeeded / Failed / Cancelled) or the token is cancelled.</summary>
    /// <param name="jobId">Job.</param>
    /// <param name="pollInterval">Delay between checks (default 1 s).</param>
    /// <param name="ct">Cancellation token (use a timeout).</param>
    Task<JobStatusResponse> WaitForCompletionAsync(Guid jobId, TimeSpan? pollInterval = null, CancellationToken ct = default);
}

/// <summary>Error returned by the JobDist API (problem details).</summary>
public sealed class JobDistApiException(HttpStatusCode status, string message) : Exception(message)
{
    /// <summary>HTTP status code.</summary>
    public HttpStatusCode StatusCode { get; } = status;
}

/// <summary>
/// HttpClient based implementation with server fail-over. Thread-safe; register as a typed client (see <see cref="JobDistClientExtensions"/>).
/// </summary>
public sealed class JobDistClient(HttpClient http, IOptions<JobDistClientOptions> options, ILogger<JobDistClient> logger) : IJobDistClient
{
    // Routes are pinned to api/v1: a server that no longer serves v1 answers 400 (unsupported version) instead of a silent change.
    private static readonly System.Net.Http.Headers.ProductInfoHeaderValue SdkAgent =
        new("JobDist.Client", typeof(JobDistClient).Assembly.GetName().Version?.ToString(3) ?? "0");

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private int _current;

    /// <inheritdoc />
    public Task<SubmitJobResponse> SubmitAsync(SubmitJobRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Idempotency key makes the POST safe to retry against another server instance.
        var safe = request.IdempotencyKey is null ? request with { IdempotencyKey = Guid.NewGuid().ToString("N") } : request;
        return SendAsync<SubmitJobResponse>(HttpMethod.Post, "api/v1/jobs", safe, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SubmitJobResponse>> SubmitBatchAsync(IReadOnlyList<SubmitJobRequest> jobs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var safe = jobs.Select(j => j.IdempotencyKey is null ? j with { IdempotencyKey = Guid.NewGuid().ToString("N") } : j).ToList();
        return SendAsync<IReadOnlyList<SubmitJobResponse>>(HttpMethod.Post, "api/v1/jobs/batch", new SubmitJobBatchRequest(safe), ct);
    }

    /// <inheritdoc />
    public Task<JobStatusResponse> GetAsync(Guid jobId, CancellationToken ct = default) =>
        SendAsync<JobStatusResponse>(HttpMethod.Get, $"api/v1/jobs/{jobId}", null, ct);

    /// <inheritdoc />
    public Task<PagedResponse<JobStatusResponse>> ListAsync(JobStatus? status = null, string? jobType = null, int skip = 0, int take = 100, CancellationToken ct = default)
    {
        var query = $"api/v1/jobs?skip={skip}&take={take}";
        if (status is not null)
        {
            query += $"&status={status}";
        }
        if (!string.IsNullOrWhiteSpace(jobType))
        {
            query += $"&jobType={Uri.EscapeDataString(jobType)}";
        }
        return SendAsync<PagedResponse<JobStatusResponse>>(HttpMethod.Get, query, null, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JobAttemptResponse>> GetAttemptsAsync(Guid jobId, CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<JobAttemptResponse>>(HttpMethod.Get, $"api/v1/jobs/{jobId}/attempts", null, ct);

    /// <inheritdoc />
    public Task<JobStatusResponse> ChangePriorityAsync(Guid jobId, int priority, CancellationToken ct = default) =>
        SendAsync<JobStatusResponse>(HttpMethod.Patch, $"api/v1/jobs/{jobId}/priority", new ChangePriorityRequest(priority), ct);

    /// <inheritdoc />
    public Task<JobStatusResponse> CancelAsync(Guid jobId, string? reason = null, CancellationToken ct = default) =>
        SendAsync<JobStatusResponse>(HttpMethod.Post, $"api/v1/jobs/{jobId}/cancel", new CancelJobRequest(reason), ct);

    /// <inheritdoc />
    public async Task<JobStatusResponse> WaitForCompletionAsync(Guid jobId, TimeSpan? pollInterval = null, CancellationToken ct = default)
    {
        var delay = pollInterval ?? TimeSpan.FromSeconds(1);
        while (true)
        {
            var job = await GetAsync(jobId, ct).ConfigureAwait(false);
            if (job.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled)
            {
                return job;
            }
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends a request, failing over across the configured servers on connection errors, timeouts and 5xx/503.
    /// 4xx responses are business errors and are not retried.
    /// </summary>
    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var o = options.Value;
        if (o.ServerUrls.Count == 0)
        {
            throw new InvalidOperationException("JobDistClient:ServerUrls is empty.");
        }
        Exception? last = null;

        // Step 1: try each server once, starting with the last one that worked.
        for (var i = 0; i < o.ServerUrls.Count; i++)
        {
            var index = (Volatile.Read(ref _current) + i) % o.ServerUrls.Count;
            var baseUrl = new Uri(o.ServerUrls[index].TrimEnd('/') + "/");
            using var request = new HttpRequestMessage(method, new Uri(baseUrl, path));
            request.Headers.Add("X-Api-Key", o.ApiKey);
            request.Headers.UserAgent.Add(SdkAgent); // lets operators see which SDK versions still call an API version before it is retired
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: Json);
            }
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(o.Timeout);
                using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);

                // Step 2: server-side failure → next server.
                if ((int)response.StatusCode >= 500)
                {
                    last = new JobDistApiException(response.StatusCode, $"Server {baseUrl.Host} returned {(int)response.StatusCode}.");
                    continue;
                }

                // Step 3: success or business error.
                Volatile.Write(ref _current, index);
                if (!response.IsSuccessStatusCode)
                {
                    throw new JobDistApiException(response.StatusCode, await ReadProblemAsync(response, ct).ConfigureAwait(false));
                }
                return (await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false))!;
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                logger.LogWarning("JobDist server {Server} unavailable ({Message}); failing over.", baseUrl.Host, ex.Message);
                last = ex;
            }
        }
        throw new JobDistApiException(HttpStatusCode.ServiceUnavailable, $"No JobDist server reachable: {last?.Message}");
    }

    private static async Task<string> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct).ConfigureAwait(false);
            if (problem.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
            {
                return d.GetString()!;
            }
            if (problem.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
            {
                return t.GetString()!;
            }
        }
        catch (JsonException)
        {
        }
        return response.ReasonPhrase ?? "Request failed";
    }
}

/// <summary>DI registration.</summary>
public static class JobDistClientExtensions
{
    /// <summary>Registers <see cref="IJobDistClient"/> bound to a configuration section.</summary>
    /// <param name="services">Services.</param>
    /// <param name="section">Section, e.g. <c>configuration.GetSection("JobDistClient")</c>.</param>
    public static IHttpClientBuilder AddJobDistClient(this IServiceCollection services, IConfiguration section)
    {
        services.AddOptions<JobDistClientOptions>().Bind(section);
        return services.AddHttpClient<IJobDistClient, JobDistClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), AllowAutoRedirect = false })
            .RemoveAllLoggers();
    }

    /// <summary>Registers <see cref="IJobDistClient"/> configured in code.</summary>
    public static IHttpClientBuilder AddJobDistClient(this IServiceCollection services, Action<JobDistClientOptions> configure)
    {
        services.AddOptions<JobDistClientOptions>().Configure(configure);
        return services.AddHttpClient<IJobDistClient, JobDistClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), AllowAutoRedirect = false })
            .RemoveAllLoggers();
    }
}
