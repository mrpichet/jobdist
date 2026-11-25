using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobDist.Contracts;

namespace JobDist.Web.Services;

/// <summary>API error with the server's RFC 7807 detail (never internal details; the server hides them).</summary>
public sealed class ApiException(HttpStatusCode status, string message, string? errorCode = null) : Exception(message)
{
    /// <summary>HTTP status.</summary>
    public HttpStatusCode Status { get; } = status;

    /// <summary>Machine-readable <c>error</c> extension (e.g. <c>queue_full</c>), when present.</summary>
    public string? ErrorCode { get; } = errorCode;

    /// <summary>409: someone else changed the record, or the job/worker state no longer allows the action.</summary>
    public bool IsConflict => Status == HttpStatusCode.Conflict;
}

/// <summary>
/// Typed client of the JobDist REST API (v1). The <see cref="HttpClient"/> carries webcommon's <c>BearerTokenHandler</c>
/// (Bearer token, 401 → sign out). GET calls are retried twice on network errors, 408, 429 and 5xx with a short back-off;
/// writes are never retried automatically: POSTs that create something carry an <c>Idempotency-Key</c> chosen by the page,
/// so pressing Save again after a lost response does not create a duplicate.
/// </summary>
public sealed class JobDistApi(HttpClient http)
{
    private const string V = "api/v1/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // ---- Jobs ----
    public Task<PagedResponse<JobStatusResponse>> ListJobsAsync(JobStatus? status, string? jobType, Guid? workerId, int skip, int take, CancellationToken ct) =>
        GetAsync<PagedResponse<JobStatusResponse>>("jobs" + Query(("status", status?.ToString()), ("jobType", jobType), ("workerId", workerId?.ToString()), ("skip", skip.ToString(Inv)), ("take", take.ToString(Inv))), ct);
    public Task<JobStatusResponse> GetJobAsync(Guid jobId, CancellationToken ct) => GetAsync<JobStatusResponse>($"jobs/{jobId}", ct);
    public Task<List<JobAttemptResponse>> GetAttemptsAsync(Guid jobId, CancellationToken ct) => GetAsync<List<JobAttemptResponse>>($"jobs/{jobId}/attempts", ct);
    public Task<RiskAssessment> GetJobRiskAsync(Guid jobId, CancellationToken ct) => GetAsync<RiskAssessment>($"jobs/{jobId}/risk", ct);
    public Task<RiskAssessment> AssessSubmissionAsync(string jobType, Guid? targetWorkerId, bool strictTarget, int? timeoutSeconds, bool sensitive, CancellationToken ct) =>
        GetAsync<RiskAssessment>("jobs/risk" + Query(("jobType", jobType), ("targetWorkerId", targetWorkerId?.ToString()), ("strictTarget", strictTarget ? "true" : null),
            ("timeoutSeconds", timeoutSeconds?.ToString(Inv)), ("sensitive", sensitive ? "true" : null)), ct);
    public Task<List<JobTypeInfoResponse>> GetJobTypesAsync(CancellationToken ct) => GetAsync<List<JobTypeInfoResponse>>("jobs/types", ct);
    public Task<SubmitJobResponse> SubmitAsync(SubmitJobRequest request, string idempotencyKey, CancellationToken ct) =>
        SendAsync<SubmitJobResponse>(HttpMethod.Post, "jobs", request, idempotencyKey, ct);
    public Task<List<SubmitJobResponse>> SubmitBatchAsync(IReadOnlyList<SubmitJobRequest> jobs, string idempotencyKey, CancellationToken ct) =>
        SendAsync<List<SubmitJobResponse>>(HttpMethod.Post, "jobs/batch", new SubmitJobBatchRequest(jobs), idempotencyKey, ct);
    public Task<JobStatusResponse> ChangePriorityAsync(Guid jobId, int priority, CancellationToken ct) =>
        SendAsync<JobStatusResponse>(HttpMethod.Patch, $"jobs/{jobId}/priority", new ChangePriorityRequest(priority), null, ct);
    public Task<JobStatusResponse> CancelJobAsync(Guid jobId, string? reason, CancellationToken ct) =>
        SendAsync<JobStatusResponse>(HttpMethod.Post, $"jobs/{jobId}/cancel", new CancelJobRequest(reason), null, ct);

    // ---- Workers ----
    public Task<List<WorkerResponse>> ListWorkersAsync(bool includeUnregistered, CancellationToken ct) =>
        GetAsync<List<WorkerResponse>>("workers" + Query(("includeUnregistered", includeUnregistered ? "true" : null)), ct);
    public Task<WorkerResponse> GetWorkerAsync(Guid workerId, CancellationToken ct) => GetAsync<WorkerResponse>($"workers/{workerId}", ct);
    public Task<RiskAssessment> GetWorkerRiskAsync(Guid workerId, CancellationToken ct) => GetAsync<RiskAssessment>($"workers/{workerId}/risk", ct);
    public Task DrainWorkerAsync(Guid workerId, CancellationToken ct) => SendAsync<object>(HttpMethod.Post, $"workers/{workerId}/drain", null, null, ct);
    public Task UnregisterWorkerAsync(Guid workerId, CancellationToken ct) => SendAsync<object>(HttpMethod.Delete, $"workers/{workerId}", null, null, ct);

    // ---- Analytics ----
    public Task<ClusterSummary> GetSummaryAsync(CancellationToken ct) => GetAsync<ClusterSummary>("admin/summary", ct);
    public Task<List<PerformanceStat>> GetPerformanceAsync(string? jobType, CancellationToken ct) => GetAsync<List<PerformanceStat>>("admin/performance" + Query(("jobType", jobType)), ct);
    public Task<List<WorkerRecommendation>> GetRecommendationsAsync(string jobType, int top, CancellationToken ct) =>
        GetAsync<List<WorkerRecommendation>>("admin/recommendations" + Query(("jobType", jobType), ("top", top.ToString(Inv))), ct);
    public Task<AnomalyReport> GetAnomaliesAsync(int windowMinutes, CancellationToken ct) => GetAsync<AnomalyReport>($"admin/anomalies?windowMinutes={windowMinutes}", ct);
    public Task<List<Anomaly>> SendAnomalyAlertAsync(int windowMinutes, string idempotencyKey, CancellationToken ct) =>
        SendAsync<List<Anomaly>>(HttpMethod.Post, $"admin/anomalies/alert?windowMinutes={windowMinutes}", null, idempotencyKey, ct);
    public Task<JobStatisticsReportResponse> GetJobStatisticsAsync(DateOnly from, DateOnly to, string? jobType, Guid? workerId, string groupBy, string timeZone, CancellationToken ct) =>
        GetAsync<JobStatisticsReportResponse>("reports/job-statistics" + Query(("fromDate", from.ToString("yyyy-MM-dd", Inv)), ("toDate", to.ToString("yyyy-MM-dd", Inv)),
            ("jobType", jobType), ("workerId", workerId?.ToString()), ("groupBy", groupBy), ("timeZone", timeZone)), ct);

    // ---- Alerts ----
    public Task<List<AlertRecipientResponse>> ListRecipientsAsync(CancellationToken ct) => GetAsync<List<AlertRecipientResponse>>("alerts/recipients", ct);
    public Task<AlertRecipientResponse> GetRecipientAsync(Guid id, CancellationToken ct) => GetAsync<AlertRecipientResponse>($"alerts/recipients/{id}", ct);
    public Task<AlertRecipientResponse> CreateRecipientAsync(AlertRecipientRequest request, string idempotencyKey, CancellationToken ct) =>
        SendAsync<AlertRecipientResponse>(HttpMethod.Post, "alerts/recipients", request, idempotencyKey, ct);
    public Task<AlertRecipientResponse> UpdateRecipientAsync(Guid id, AlertRecipientRequest request, CancellationToken ct) =>
        SendAsync<AlertRecipientResponse>(HttpMethod.Put, $"alerts/recipients/{id}", request, null, ct);
    public Task DeleteRecipientAsync(Guid id, long version, CancellationToken ct) => SendAsync<object>(HttpMethod.Delete, $"alerts/recipients/{id}?version={version}", null, null, ct);
    public Task<AlertSendResultResponse> TestRecipientAsync(Guid id, string idempotencyKey, CancellationToken ct) =>
        SendAsync<AlertSendResultResponse>(HttpMethod.Post, $"alerts/recipients/{id}/test", null, idempotencyKey, ct);
    public Task<List<string>> GetChannelsAsync(CancellationToken ct) => GetAsync<List<string>>("alerts/channels", ct);
    public Task<PagedResponse<AlertLogResponse>> ListAlertLogsAsync(DateTimeOffset? fromUtc, DateTimeOffset? toUtc, AlertEventType? eventType, AlertSendStatus? status, int skip, int take, CancellationToken ct) =>
        GetAsync<PagedResponse<AlertLogResponse>>("alerts/logs" + Query(("fromUtc", fromUtc?.ToString("o", Inv)), ("toUtc", toUtc?.ToString("o", Inv)), ("eventType", eventType?.ToString()),
            ("status", status?.ToString()), ("skip", skip.ToString(Inv)), ("take", take.ToString(Inv))), ct);

    // ---- Queue thresholds ----
    public Task<List<QueueThresholdResponse>> ListThresholdsAsync(CancellationToken ct) => GetAsync<List<QueueThresholdResponse>>("alerts/queue-thresholds", ct);
    public Task<QueueThresholdResponse> GetThresholdAsync(Guid id, CancellationToken ct) => GetAsync<QueueThresholdResponse>($"alerts/queue-thresholds/{id}", ct);
    public Task<QueueThresholdResponse> CreateThresholdAsync(QueueThresholdRequest request, string idempotencyKey, CancellationToken ct) =>
        SendAsync<QueueThresholdResponse>(HttpMethod.Post, "alerts/queue-thresholds", request, idempotencyKey, ct);
    public Task<QueueThresholdResponse> UpdateThresholdAsync(Guid id, QueueThresholdRequest request, CancellationToken ct) =>
        SendAsync<QueueThresholdResponse>(HttpMethod.Put, $"alerts/queue-thresholds/{id}", request, null, ct);
    public Task DeleteThresholdAsync(Guid id, long version, CancellationToken ct) => SendAsync<object>(HttpMethod.Delete, $"alerts/queue-thresholds/{id}?version={version}", null, null, ct);
    public Task<List<QueueStatusResponse>> GetQueueStatusAsync(CancellationToken ct) => GetAsync<List<QueueStatusResponse>>("alerts/queue-status", ct);

    // ---- Browser logs ----
    /// <summary>Sends browser log entries (no retry, no logging: a failure here must never log again).</summary>
    public async Task<bool> SendClientLogsAsync(ClientLogBatchRequest batch, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(V + "client-logs", batch, Json, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static readonly IFormatProvider Inv = System.Globalization.CultureInfo.InvariantCulture;

    private static string Query(params (string Key, string? Value)[] pairs)
    {
        var parts = pairs.Where(p => !string.IsNullOrEmpty(p.Value)).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}").ToList();
        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        // Retry: 2 more tries after 300 ms and 900 ms (with ±20 % jitter) for transient failures only.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await SendAsync<T>(HttpMethod.Get, url, null, null, ct);
            }
            catch (Exception ex) when (attempt < 2 && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                var delay = 300 * Math.Pow(3, attempt) * (0.8 + Random.Shared.NextDouble() * 0.4);
                await Task.Delay(TimeSpan.FromMilliseconds(delay), ct);
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException => true,
        ApiException a => a.Status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)a.Status >= 500,
        _ => false
    };

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body, string? idempotencyKey, CancellationToken ct)
    {
        // Step 1: request (v1 route, JSON body, optional Idempotency-Key).
        using var request = new HttpRequestMessage(method, V + url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        // Step 2: send; map problem details to ApiException.
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await ToExceptionAsync(response, ct);
        }
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Accepted || typeof(T) == typeof(object))
        {
            return default!;
        }
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private static async Task<ApiException> ToExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? detail = null, title = null, error = null;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            if (problem.ValueKind == JsonValueKind.Object)
            {
                detail = problem.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                title = problem.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                error = problem.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                if (detail is null && problem.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Object)
                {
                    detail = string.Join(" ", errs.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(v => v.GetString())));
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Not a problem-details body.
        }
        return new ApiException(response.StatusCode, detail ?? title ?? response.ReasonPhrase ?? response.StatusCode.ToString(), error);
    }
}
