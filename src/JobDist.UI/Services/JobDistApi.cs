using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobDist.Contracts;
using Microsoft.JSInterop;

namespace JobDist.UI.Services;

/// <summary>
/// Keeps the operator's API key in <c>sessionStorage</c> (cleared when the tab closes; never in localStorage or cookies, so it is
/// not sent automatically and cannot be abused through CSRF).
/// </summary>
public sealed class ApiKeyStore(IJSRuntime js)
{
    private const string StorageKey = "jobdist.apiKey";
    private string? _key;

    /// <summary>Current key (null = not signed in).</summary>
    public string? Key => _key;

    /// <summary>Raised when the key changes.</summary>
    public event Action? Changed;

    /// <summary>Loads the key from session storage.</summary>
    public async Task LoadAsync()
    {
        _key = await js.InvokeAsync<string?>("sessionStorage.getItem", StorageKey);
        Changed?.Invoke();
    }

    /// <summary>Stores (or clears with null) the key.</summary>
    public async Task SetAsync(string? key)
    {
        _key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        if (_key is null)
        {
            await js.InvokeVoidAsync("sessionStorage.removeItem", StorageKey);
        }
        else
        {
            await js.InvokeVoidAsync("sessionStorage.setItem", StorageKey, _key);
        }
        Changed?.Invoke();
    }
}

/// <summary>Cluster summary as returned by <c>GET /api/admin/summary</c>.</summary>
public sealed record ClusterSummary(int WorkersOnline, int WorkersDraining, int WorkersOffline, int TotalCapacity, int ActiveJobs,
    Dictionary<string, int> JobsByStatus, Dictionary<string, int> PendingByType, string? LeaderInstance, bool ThisInstanceIsLeader, DateTimeOffset GeneratedUtc);

/// <summary>Risk factor.</summary>
public sealed record RiskFactor(string Factor, double Points, string Explanation);

/// <summary>Risk assessment.</summary>
public sealed record RiskAssessment(string Subject, double Score, string Level, List<RiskFactor> Factors, string Recommendation);

/// <summary>Anomaly.</summary>
public sealed record Anomaly(string Kind, string Severity, string Subject, string Description, double ObservedValue, double ExpectedValue, double Score, DateTimeOffset DetectedUtc);

/// <summary>Anomaly report.</summary>
public sealed record AnomalyReport(DateTimeOffset FromUtc, DateTimeOffset ToUtc, List<Anomaly> Anomalies);

/// <summary>Learned performance per worker / type.</summary>
public sealed record PerformanceStat(Guid WorkerId, string JobType, int Samples, int Successes, int Failures, double MeanDurationMs, double StdDevDurationMs, DateTimeOffset LastUpdatedUtc);

/// <summary>Typed client for the JobDist REST API used by the console pages.</summary>
public sealed class JobDistApi(HttpClient http, ApiKeyStore keys)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public Task<ClusterSummary?> GetSummaryAsync() => GetAsync<ClusterSummary>("api/admin/summary");
    public Task<PagedResponse<JobStatusResponse>?> GetJobsAsync(JobStatus? status, string? jobType, int take) =>
        GetAsync<PagedResponse<JobStatusResponse>>($"api/jobs?take={take}{(status is null ? "" : $"&status={status}")}{(string.IsNullOrWhiteSpace(jobType) ? "" : $"&jobType={Uri.EscapeDataString(jobType)}")}");
    public Task<List<JobAttemptResponse>?> GetAttemptsAsync(Guid jobId) => GetAsync<List<JobAttemptResponse>>($"api/jobs/{jobId}/attempts");
    public Task<RiskAssessment?> GetJobRiskAsync(Guid jobId) => GetAsync<RiskAssessment>($"api/jobs/{jobId}/risk");
    public Task<List<WorkerResponse>?> GetWorkersAsync() => GetAsync<List<WorkerResponse>>("api/workers?includeUnregistered=true");
    public Task<RiskAssessment?> GetWorkerRiskAsync(Guid workerId) => GetAsync<RiskAssessment>($"api/workers/{workerId}/risk");
    public Task<AnomalyReport?> GetAnomaliesAsync(int windowMinutes) => GetAsync<AnomalyReport>($"api/admin/anomalies?windowMinutes={windowMinutes}");
    public Task<List<PerformanceStat>?> GetPerformanceAsync() => GetAsync<List<PerformanceStat>>("api/admin/performance");

    public Task<SubmitJobResponse?> SubmitAsync(SubmitJobRequest request) => SendAsync<SubmitJobResponse>(HttpMethod.Post, "api/jobs", request);
    public Task<JobStatusResponse?> CancelAsync(Guid jobId, string? reason) => SendAsync<JobStatusResponse>(HttpMethod.Post, $"api/jobs/{jobId}/cancel", new CancelJobRequest(reason));
    public Task<JobStatusResponse?> ChangePriorityAsync(Guid jobId, int priority) => SendAsync<JobStatusResponse>(HttpMethod.Patch, $"api/jobs/{jobId}/priority", new ChangePriorityRequest(priority));
    public Task<object?> DrainAsync(Guid workerId) => SendAsync<object>(HttpMethod.Post, $"api/workers/{workerId}/drain", null);

    /// <summary>GET with the API key header; throws <see cref="ApiException"/> with the server's problem detail on failure.</summary>
    private async Task<T?> GetAsync<T>(string url) => await SendAsync<T>(HttpMethod.Get, url, null);

    private async Task<T?> SendAsync<T>(HttpMethod method, string url, object? body)
    {
        // Step 1: build request with the key from session storage.
        using var request = new HttpRequestMessage(method, url);
        if (keys.Key is not null)
        {
            request.Headers.Add("X-Api-Key", keys.Key);
        }
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        // Step 2: send and map errors to a readable message.
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            string detail;
            try
            {
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
                detail = problem.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! :
                         problem.TryGetProperty("title", out var t) ? t.GetString() ?? response.ReasonPhrase ?? "" : response.ReasonPhrase ?? "";
            }
            catch (JsonException)
            {
                detail = response.ReasonPhrase ?? "Request failed";
            }
            throw new ApiException((int)response.StatusCode, detail);
        }
        if (response.Content.Headers.ContentLength == 0 || response.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            return default;
        }
        return await response.Content.ReadFromJsonAsync<T>(Json);
    }
}

/// <summary>API error with HTTP status and the server's safe message.</summary>
public sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
