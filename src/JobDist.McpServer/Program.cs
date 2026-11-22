// =============================================================================================
// JobDist MCP server (stdio transport).
// A desktop AI client starts this process and talks JSON-RPC over stdin/stdout. It connects to the
// SAME database as the JobDist servers (SQL Server or PostgreSQL), so it sees the live cluster.
// IMPORTANT: stdout is the protocol channel, so every log line goes to stderr.
// =============================================================================================
using JobDist.Infrastructure;
using JobDist.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Step 1: route every log line to stderr (keeps stdout clean for MCP JSON-RPC).
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Step 2: same business layer as the Web API; no scheduler/leader/background workers in this client-side process.
builder.Services.AddJobDist(builder.Configuration, runBackgroundWorkers: false);
builder.Services.AddJobDistAiTools();

// Step 3: MCP server with the Microsoft.Extensions.AI based tools (side-effect tools only when explicitly enabled).
builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "JobDist", Version = "1.0.0" })
    .WithStdioServerTransport()
    .WithJobDistTools(builder.Configuration.GetValue("Mcp:AllowSideEffects", false));

await builder.Build().RunAsync().ConfigureAwait(false);
