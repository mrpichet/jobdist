// =============================================================================================
// JobDist.Server — one process, three front doors over the same business layer (JobDist.Core):
//   1) REST Web API (ASP.NET Core controllers)              → /api/*
//   2) MCP server (Streamable HTTP, official C# SDK)        → /mcp   (tools = Microsoft.Extensions.AI AIFunctions)
//   3) Blazor WebAssembly web console (JobDist.Web, mrpichet/webcommon design) → /
// Authentication: X-Api-Key (workers, SDK, MCP, scripts) and, when AuthServer:Authority is set, AuthServer Bearer tokens (web console).
// Logging: Common.Logging → LogServer. Alerts: transactional outbox → MessageHub (directly or through RabbitMQ).
// Run several instances behind a load balancer (or let workers fail over between URLs): every instance serves the API,
// a database lease elects one leader for scheduling / fail-over / anomaly scans (no single point of failure).
// =============================================================================================
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Common.AspNetCore.Authentication;
using Common.Logging;
using JobDist.Core.Services;
using JobDist.Infrastructure;
using JobDist.Mcp;
using JobDist.Server.Console;
using JobDist.Server.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);

// Step 1: observability (OpenTelemetry → Aspire dashboard over OTLP), health checks and LogServer (Logging:LogServer;
// no ServerUrls = disabled; API key from environment LOGSERVER_API_KEY).
builder.AddServiceDefaults();
builder.Logging.AddLogServer(builder.Configuration);

// Step 2: Kestrel hardening - no server header, bounded body / header sizes, slow-client protection.
builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
    k.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
    k.Limits.MinRequestBodyDataRate = new(240, TimeSpan.FromSeconds(10));
    k.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
});

// Step 3: business layer (DAO/ADO for the configured database, services, background workers) + AI tools.
builder.Services.AddJobDist(builder.Configuration);
builder.Services.AddJobDistAiTools();
builder.Services.AddOptions<AgentChatOptions>().Bind(builder.Configuration.GetSection(AgentChatOptions.SectionName)).ValidateDataAnnotations();
builder.Services.AddOptions<ClientLoggingOptions>().Bind(builder.Configuration.GetSection(ClientLoggingOptions.SectionName));
builder.Services.AddSingleton<AgentChatService>();
builder.Services.AddSingleton<ClientLogForwarder>();

// Step 4: REST controllers, string enums, RFC 7807 problem details, OpenAPI document.
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<JobDistExceptionHandler>();
builder.Services.AddScoped<WorkerTokenFilter>();
builder.Services.AddOpenApi();

// Step 4b: API versioning (same scheme as AuthServer): /api/v1/... and the unversioned /api/... alias = v1;
// also header api-version or query ?api-version; responses carry api-supported-versions.
builder.Services.AddApiVersioning(o =>
{
    o.DefaultApiVersion = new ApiVersion(1, 0);
    o.AssumeDefaultVersionWhenUnspecified = true;
    o.ReportApiVersions = true;
    o.ApiVersionReader = ApiVersionReader.Combine(new UrlSegmentApiVersionReader(), new HeaderApiVersionReader("api-version"), new QueryStringApiVersionReader("api-version"));
}).AddMvc();

// Step 5: API-key authentication (hashed) + role policies; deny by default (fallback policy).
// AuthServer Bearer tokens are accepted next to API keys when AuthServer:Authority is set (Common.AspNetCore validates them
// locally against the cached JWKS). Endpoints use "permission OR API-key role" policies ([JobDistAuthorize]).
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));
builder.Services.AddSingleton<IAuthorizationPolicyProvider, JobDistPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, JobDistAccessHandler>();
builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
var authServerEnabled = builder.Services.AddAuthServerAlongsideExistingScheme(builder.Configuration);
builder.Services.AddSingleton(new AuthSchemes(authServerEnabled
    ? [ApiKeyAuthenticationHandler.SchemeName, AuthServerAuthenticationHandler.SchemeName]
    : [ApiKeyAuthenticationHandler.SchemeName]));
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(Policies.Read, p => p.RequireRole(Roles.Viewer, Roles.Submitter, Roles.Admin))
    .AddPolicy(Policies.Submit, p => p.RequireRole(Roles.Submitter, Roles.Admin))
    .AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin))
    .AddPolicy(Policies.Worker, p => p.RequireRole(Roles.Worker))
    .AddPolicy(Policies.AiAgent, p => p.RequireRole(Roles.AiAgent, Roles.Admin));

// Step 6: rate limiting per client (generous global bucket for throughput, tighter limits for registration / anonymous).
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetTokenBucketLimiter(PartitionKey(ctx), _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 20_000, TokensPerPeriod = 10_000, ReplenishmentPeriod = TimeSpan.FromSeconds(1), QueueLimit = 0, AutoReplenishment = true
        }));
    o.AddPolicy("submit", ctx => RateLimitPartition.GetTokenBucketLimiter(PartitionKey(ctx), _ => new TokenBucketRateLimiterOptions
    {
        TokenLimit = 5_000, TokensPerPeriod = 2_500, ReplenishmentPeriod = TimeSpan.FromSeconds(1), QueueLimit = 0, AutoReplenishment = true
    }));
    o.AddPolicy("register", ctx => RateLimitPartition.GetFixedWindowLimiter(PartitionKey(ctx), _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
    }));
    o.AddPolicy("agent", ctx => RateLimitPartition.GetFixedWindowLimiter(PartitionKey(ctx), _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
    }));
    o.AddPolicy("client-logs", ctx => RateLimitPartition.GetFixedWindowLimiter(PartitionKey(ctx), _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
    }));
});

// Step 7: CORS only for configured origins (the bundled UI is same-origin and needs none).
var origins = builder.Configuration.GetSection("Security:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins)
    .WithHeaders("Content-Type", "Authorization", ApiKeyAuthenticationHandler.HeaderName, IdempotentAttribute.HeaderName, "api-version", "Mcp-Session-Id")
    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")));

// Step 7b: same-origin reverse proxy to AuthServer for the web console login (Q16): /api/v1/auth/* and /api/v1/account/*
// (webcommon) are forwarded to AuthServer:Authority, so the browser never needs CORS to AuthServer. The unversioned
// /api/auth/* and /api/account/* (older webcommon builds) are still accepted and rewritten to AuthServer's /api/v1/...
var authority = builder.Configuration["AuthServer:Authority"];
var proxyAuthServer = authServerEnabled && Uri.TryCreate(authority, UriKind.Absolute, out _);
if (proxyAuthServer)
{
    // AuthServer only serves api/v1/... (unversioned paths are 404 there). When AuthServer ships v2, webcommon moves to it;
    // until then these target patterns stay on v1.
    RouteConfig Route(string id, string path, string target) => new()
    {
        RouteId = id, ClusterId = "authserver", AuthorizationPolicy = "anonymous",
        Match = new RouteMatch { Path = path },
        Transforms = [new Dictionary<string, string> { ["PathPattern"] = target }]
    };
    builder.Services.AddReverseProxy().LoadFromMemory(
        [Route("authserver-auth-v1", "/api/v1/auth/{**rest}", "/api/v1/auth/{**rest}"),
         Route("authserver-account-v1", "/api/v1/account/{**rest}", "/api/v1/account/{**rest}"),
         Route("authserver-auth", "/api/auth/{**rest}", "/api/v1/auth/{**rest}"),
         Route("authserver-account", "/api/account/{**rest}", "/api/v1/account/{**rest}")],
        [new ClusterConfig
        {
            ClusterId = "authserver",
            Destinations = new Dictionary<string, DestinationConfig> { ["primary"] = new() { Address = authority! } },
            HttpRequest = new Yarp.ReverseProxy.Forwarder.ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(30) }
        }])
        // AuthServer announces retirement of an API version with Deprecation / Sunset headers (its CLAUDE.md §1.1): log them.
        .AddTransforms(t => t.AddResponseTransform(ctx =>
        {
            if (ctx.ProxyResponse is { } response)
            {
                ctx.HttpContext.RequestServices.GetRequiredService<JobDist.Infrastructure.Http.UpstreamVersionMonitor>()
                    .Observe("AuthServer", response.RequestMessage?.RequestUri?.AbsolutePath ?? ctx.HttpContext.Request.Path, response.Headers);
            }
            return ValueTask.CompletedTask;
        }));
}

// Step 8: MCP server over Streamable HTTP; stateless → any instance can serve any request (horizontal scale / HA).
var allowSideEffects = builder.Configuration.GetValue("Mcp:AllowSideEffects", false);
builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "JobDist", Version = "1.0.0" })
    .WithHttpTransport(o => o.Stateless = true)
    .WithJobDistTools(allowSideEffects);

// Step 9: forwarded headers only from known proxies (configure KnownProxies / KnownNetworks for your load balancer).
builder.Services.Configure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var app = builder.Build();

// ------------------------------------------------------------------ middleware pipeline
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
if (app.Configuration.GetValue("Security:HttpsRedirection", false))
{
    app.UseHttpsRedirection();
}
app.UseMiddleware<SecurityHeadersMiddleware>();

// Blazor WebAssembly UI (static files are public; every API call from the UI carries the user's API key).
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseMiddleware<ApiUsageMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}
app.MapDefaultEndpoints();
app.MapControllers();
if (proxyAuthServer)
{
    app.MapReverseProxy();
}
app.MapMcp("/mcp").RequireAuthorization(Policies.AiAgent);
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

// Rate-limit partition: authenticated client name, otherwise the remote IP.
static string PartitionKey(HttpContext ctx) =>
    ctx.User.Identity?.IsAuthenticated == true ? $"key:{ctx.User.Identity.Name}" : $"ip:{ctx.Connection.RemoteIpAddress}";

/// <summary>Entry point marker (enables WebApplicationFactory in integration tests).</summary>
public partial class Program;
