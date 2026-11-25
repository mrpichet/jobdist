using System.ClientModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Security.Claims;
using Common.AspNetCore.Authentication;
using JobDist.Contracts;
using JobDist.Mcp;
using JobDist.Server.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

namespace JobDist.Server.Console;

/// <summary>AI assistant settings (section <c>AgentChat</c>); disabled until the installer configures a provider.</summary>
public sealed class AgentChatOptions
{
    public const string SectionName = "AgentChat";

    /// <summary>Turn the assistant on.</summary>
    public bool Enabled { get; set; }

    /// <summary><c>OpenAI</c>, <c>AzureOpenAI</c> or <c>OpenAICompatible</c> (Ollama, vLLM, LM Studio ... any OpenAI-compatible endpoint).</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Endpoint URL (empty = api.openai.com for <c>OpenAI</c>). Azure: <c>https://{resource}.openai.azure.com/openai/v1/</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Model or deployment name.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>API key. Supply via environment <c>AgentChat__ApiKey</c> or a secret store, never commit.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Messages of history sent to the model.</summary>
    [Range(1, 50)] public int MaxHistory { get; set; } = 20;

    /// <summary>Timeout of one chat request.</summary>
    [Range(5, 600)] public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// Chat for the web console: a Microsoft.Extensions.AI chat client with JobDist's read-only tools, filtered by the caller's
/// permissions (no <c>jobdist.worker.view</c> → no <c>list_workers</c>). Chat text is never logged, only timing.
/// A simple circuit breaker (5 failures in a row → open 30 s) returns 503 quickly while the provider is down. No retries.
/// </summary>
public sealed class AgentChatService
{
    private static readonly Dictionary<string, string> ToolPermissions = new(StringComparer.Ordinal)
    {
        ["get_job_status"] = Permissions.JobView,
        ["list_jobs"] = Permissions.JobView,
        ["get_job_history"] = Permissions.JobView,
        ["assess_job_risk"] = Permissions.JobView,
        ["assess_submission_risk"] = Permissions.JobView,
        ["list_workers"] = Permissions.WorkerView,
        ["assess_worker_risk"] = Permissions.WorkerView,
        ["get_cluster_summary"] = Permissions.DashboardView,
        ["get_queue_status"] = Permissions.DashboardView,
        ["get_worker_performance"] = Permissions.InsightView,
        ["recommend_workers"] = Permissions.InsightView,
        ["detect_anomalies"] = Permissions.InsightView,
        ["list_alert_logs"] = Permissions.AlertView,
        ["get_job_statistics"] = Permissions.ReportView,
    };

    private readonly IReadOnlyList<AIFunction> _tools = JobDistAiFunctions.Create(includeSideEffects: false);
    private readonly AgentChatOptions _options;
    private readonly IChatClient? _client;
    private readonly IOptions<AuthServerOptions> _authServer;
    private readonly ILogger<AgentChatService> _logger;
    private readonly Lock _gate = new();
    private int _failures;
    private DateTimeOffset _openUntil;

    /// <summary>Builds the provider client once (null when disabled or incomplete).</summary>
    public AgentChatService(IOptions<AgentChatOptions> options, IOptions<AuthServerOptions> authServer, IServiceProvider services, ILogger<AgentChatService> logger)
    {
        _options = options.Value;
        _authServer = authServer;
        _logger = logger;
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Model) || string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return;
        }
        var clientOptions = new OpenAIClientOptions { NetworkTimeout = TimeSpan.FromSeconds(_options.TimeoutSeconds), RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(0) };
        if (!string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            clientOptions.Endpoint = new Uri(_options.Endpoint);
        }
        else if (!_options.Provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("AgentChat:Endpoint is required for provider {Provider}; the assistant stays disabled.", _options.Provider);
            return;
        }
        _client = new OpenAIClient(new ApiKeyCredential(_options.ApiKey), clientOptions)
            .GetChatClient(_options.Model)
            .AsIChatClient()
            .AsBuilder()
            .UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = 8)
            .Build(services);
    }

    /// <summary><c>true</c> when a provider is configured.</summary>
    public bool IsEnabled => _client is not null;

    /// <summary>Answers a chat.</summary>
    /// <exception cref="Core.Services.ValidationException">Too many messages.</exception>
    public async Task<AgentChatResponse> ChatAsync(AgentChatRequest request, ClaimsPrincipal user, IServiceProvider requestServices, CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("AgentChat is not configured.");
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow < _openUntil)
            {
                return new AgentChatResponse(request.Language == "en"
                    ? "The AI service is temporarily unavailable. Please try again shortly."
                    : "บริการ AI ไม่พร้อมใช้งานชั่วคราว กรุณาลองใหม่อีกครั้งภายหลัง");
            }
        }

        // Step 1: tools the caller may use.
        var perms = user.Permissions();
        var isAdmin = user.Identities.Any(i => i.IsAuthenticated && i.AuthenticationType == AuthServerAuthenticationHandler.SchemeName && i.HasClaim(ClaimTypes.Role, _authServer.Value.AdminRole));
        var tools = _tools.Where(t => isAdmin || (ToolPermissions.TryGetValue(t.Name, out var p) && perms.Contains(p))).Cast<AITool>().ToList();

        // Step 2: messages (system prompt + last N).
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "You are the assistant of JobDist, a distributed job scheduler, inside its web console. Answer briefly and factually " +
                $"in {(request.Language == "en" ? "English" : "Thai")}. Use the tools to read live data; never invent ids or numbers. " +
                "You cannot change anything: for actions, tell the user which screen to use. " +
                $"The user is on page '{request.Page ?? "-"}'. Times from tools are UTC; show them in Asia/Bangkok (UTC+7).")
        };
        messages.AddRange(request.Messages.TakeLast(_options.MaxHistory).Select(m => new ChatMessage(m.Role == "assistant" ? ChatRole.Assistant : ChatRole.User, m.Content)));

        // Step 3: call with timeout.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await client.GetResponseAsync(messages, new ChatOptions { Tools = tools }, cts.Token).ConfigureAwait(false);
            lock (_gate)
            {
                _failures = 0;
            }
            _logger.LogInformation("Agent chat answered in {ElapsedMs} ms with {Tools} tool(s) available, usage {InputTokens}/{OutputTokens} tokens.",
                sw.ElapsedMilliseconds, tools.Count, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount);
            return new AgentChatResponse(response.Text);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (++_failures >= 5)
                {
                    _openUntil = DateTimeOffset.UtcNow.AddSeconds(30);
                    _failures = 0;
                }
            }
            _logger.LogWarning("Agent chat failed after {ElapsedMs} ms: {Error}", sw.ElapsedMilliseconds, ex.GetType().Name);
            return new AgentChatResponse(request.Language == "en"
                ? "Sorry, the AI service did not respond. Please try again."
                : "ขออภัย บริการ AI ไม่ตอบสนอง กรุณาลองใหม่อีกครั้ง");
        }
    }
}
