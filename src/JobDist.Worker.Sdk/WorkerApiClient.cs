using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobDist.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobDist.Worker.Sdk;

/// <summary>Error returned by the server to a worker call.</summary>
/// <param name="status">HTTP status (503 when no server was reachable).</param>
/// <param name="message">Problem detail.</param>
public sealed class WorkerApiException(HttpStatusCode status, string message) : Exception(message)
{
    /// <summary>HTTP status code.</summary>
    public HttpStatusCode StatusCode { get; } = status;

    /// <summary>True when the worker identity is no longer valid and it must register again (403 token / 404 worker).</summary>
    public bool RequiresReRegistration => StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound;

    /// <summary>True when the server could not be reached or failed (retry later).</summary>
    public bool IsTransient => StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests || (int)StatusCode >= 500;
}

/// <summary>
/// Thin HTTP client for the worker protocol (<c>/api/workers/...</c>). Adds <c>X-Api-Key</c> and <c>X-Worker-Token</c>
/// and fails over between the configured server instances on connection errors, timeouts and 5xx.
/// Thread-safe.
/// </summary>
public sealed class WorkerApiClient(HttpClient http, IOptions<JobDistWorkerOptions> options, ILogger<WorkerApiClient> logger)
{
    // Routes are pinned to api/v1: a server that no longer serves v1 answers 400 (unsupported version) instead of a silent change.
    private static readonly System.Net.Http.Headers.ProductInfoHeaderValue SdkAgent =
        new("JobDist.Worker.Sdk", typeof(WorkerApiClient).Assembly.GetName().Version?.ToString(3) ?? "0");

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private int _current;

    /// <summary>Worker id after registration.</summary>
    public Guid WorkerId { get; private set; }

    /// <summary>Secret worker token after registration (kept only in memory).</summary>
    private string? _token;

    /// <summary>Registers the worker and remembers id and token for later calls.</summary>
    /// <param name="request">Registration data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Registration response including server keys.</returns>
    public async Task<RegisterWorkerResponse> RegisterAsync(RegisterWorkerRequest request, CancellationToken ct)
    {
        // Step 1: register (fleet API key only, no worker token yet).
        var response = await SendAsync<RegisterWorkerResponse>(HttpMethod.Post, "api/v1/workers/register", request, null, ct).ConfigureAwait(false);

        // Step 2: keep the identity for the authenticated worker calls.
        WorkerId = response!.WorkerId;
        _token = response.WorkerToken;
        return response;
    }

    /// <summary>Unregisters the worker (graceful shutdown).</summary>
    /// <param name="ct">Cancellation token.</param>
    public Task UnregisterAsync(CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Delete, $"api/v1/workers/{WorkerId}", null, null, ct);

    /// <summary>Sends a heartbeat.</summary>
    /// <param name="request">Load figures.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Server status and pending commands.</returns>
    public async Task<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest request, CancellationToken ct) =>
        (await SendAsync<HeartbeatResponse>(HttpMethod.Post, $"api/v1/workers/{WorkerId}/heartbeat", request, null, ct).ConfigureAwait(false))!;

    /// <summary>Long-polls for assignments.</summary>
    /// <param name="request">Max jobs and wait time.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Assignments and commands.</returns>
    public async Task<PollResponse> PollAsync(PollRequest request, CancellationToken ct) =>
        (await SendAsync<PollResponse>(HttpMethod.Post, $"api/v1/workers/{WorkerId}/poll", request,
            TimeSpan.FromSeconds(request.WaitSeconds) + options.Value.RequestTimeout, ct).ConfigureAwait(false))!;

    /// <summary>Reports that an attempt started.</summary>
    /// <param name="jobId">Job.</param>
    /// <param name="attemptId">Attempt.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the job was cancelled meanwhile and must not run.</returns>
    public async Task<bool> StartedAsync(Guid jobId, Guid attemptId, CancellationToken ct)
    {
        var r = await SendAsync<JsonElement>(HttpMethod.Post, $"api/v1/workers/{WorkerId}/jobs/{jobId}/started", new JobStartedRequest(attemptId), null, ct).ConfigureAwait(false);
        return r.ValueKind == JsonValueKind.Object && r.TryGetProperty("cancelRequested", out var c) && c.ValueKind == JsonValueKind.True;
    }

    /// <summary>Reports the end of an attempt.</summary>
    /// <param name="jobId">Job.</param>
    /// <param name="report">Outcome.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task CompletedAsync(Guid jobId, JobCompletedRequest report, CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Post, $"api/v1/workers/{WorkerId}/jobs/{jobId}/completed", report, null, ct);

    /// <summary>Downloads the server's current public signing keys (anonymous endpoint).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Keys.</returns>
    public async Task<ServerKeysResponse> GetServerKeysAsync(CancellationToken ct) =>
        (await SendAsync<ServerKeysResponse>(HttpMethod.Get, "api/v1/keys", null, null, ct).ConfigureAwait(false))!;

    /// <summary>
    /// Sends a request with fail-over: each configured server is tried once, starting with the last healthy one.
    /// </summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Relative path.</param>
    /// <param name="body">JSON body or null.</param>
    /// <param name="timeout">Per-try timeout (default <see cref="JobDistWorkerOptions.RequestTimeout"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Deserialized body, or default for 204.</returns>
    /// <exception cref="WorkerApiException">4xx business errors, or 503 when no server answered.</exception>
    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, TimeSpan? timeout, CancellationToken ct)
    {
        var o = options.Value;
        Exception? last = null;
        for (var i = 0; i < o.ServerUrls.Count; i++)
        {
            // Step 1: build the request for the next server in the ring.
            var index = (Volatile.Read(ref _current) + i) % o.ServerUrls.Count;
            var baseUrl = new Uri(o.ServerUrls[index].TrimEnd('/') + "/");
            using var request = new HttpRequestMessage(method, new Uri(baseUrl, path));
            request.Headers.Add("X-Api-Key", o.ApiKey);
            request.Headers.UserAgent.Add(SdkAgent); // lets operators see which SDK versions still call an API version before it is retired
            if (_token is not null)
            {
                request.Headers.Add("X-Worker-Token", _token);
            }
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: Json);
            }

            try
            {
                // Step 2: send with a per-try timeout so a dead node does not block fail-over.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout ?? o.RequestTimeout);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

                // Step 3: 5xx / 503 → try another instance (HA).
                if ((int)response.StatusCode >= 500)
                {
                    last = new WorkerApiException(response.StatusCode, $"{baseUrl.Host} returned {(int)response.StatusCode}");
                    continue;
                }

                // Step 4: remember the healthy node; map business errors.
                Volatile.Write(ref _current, index);
                if (!response.IsSuccessStatusCode)
                {
                    throw new WorkerApiException(response.StatusCode, await ReadProblemAsync(response, ct).ConfigureAwait(false));
                }
                if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
                {
                    return default;
                }
                return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                // Step 5: network failure or timeout → fail over.
                logger.LogDebug("Server {Server} unreachable: {Message}", baseUrl.Host, ex.Message);
                last = ex;
            }
        }
        throw new WorkerApiException(HttpStatusCode.ServiceUnavailable, $"No JobDist server reachable: {last?.Message}");
    }

    /// <summary>Extracts <c>detail</c> from a problem-details body.</summary>
    private static async Task<string> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct).ConfigureAwait(false);
            if (problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
            {
                return d.GetString()!;
            }
        }
        catch (JsonException)
        {
            // Non-JSON error body: fall through to the status text.
        }
        return $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
    }
}
