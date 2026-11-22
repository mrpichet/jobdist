// =============================================================================================
// JobDist.Server — one process, three front doors over the same business layer (JobDist.Core):
//   1) REST Web API (ASP.NET Core controllers)              → /api/*
//   2) MCP server (Streamable HTTP, official C# SDK)        → /mcp   (tools = Microsoft.Extensions.AI AIFunctions)
//   3) Blazor WebAssembly UI with DevExpress components     → /
// Run several instances behind a load balancer (or let workers fail over between URLs): every instance serves the API,
// a database lease elects one leader for scheduling / fail-over / anomaly scans (no single point of failure).
// =============================================================================================
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using JobDist.Infrastructure;
using JobDist.Mcp;
using JobDist.Server.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Step 1: observability (OpenTelemetry → Aspire dashboard over OTLP) and health checks.
builder.AddServiceDefaults();

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

// Step 4: REST controllers, string enums, RFC 7807 problem details, OpenAPI document.
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<JobDistExceptionHandler>();
builder.Services.AddScoped<WorkerTokenFilter>();
builder.Services.AddOpenApi();

// Step 5: API-key authentication (hashed) + role policies; deny by default (fallback policy).
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));
builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
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
});

// Step 7: CORS only for configured origins (the bundled UI is same-origin and needs none).
var origins = builder.Configuration.GetSection("Security:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).WithMethods("GET", "POST", "PATCH", "DELETE")
    .WithHeaders("Content-Type", ApiKeyAuthenticationHandler.HeaderName, "Mcp-Session-Id")));

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

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}
app.MapDefaultEndpoints();
app.MapControllers();
app.MapMcp("/mcp").RequireAuthorization(Policies.AiAgent);
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

// Rate-limit partition: authenticated client name, otherwise the remote IP.
static string PartitionKey(HttpContext ctx) =>
    ctx.User.Identity?.IsAuthenticated == true ? $"key:{ctx.User.Identity.Name}" : $"ip:{ctx.Connection.RemoteIpAddress}";

/// <summary>Entry point marker (enables WebApplicationFactory in integration tests).</summary>
public partial class Program;
