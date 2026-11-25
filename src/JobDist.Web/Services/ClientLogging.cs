using System.Collections.Concurrent;
using JobDist.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebCommon.UI.Auth;

namespace JobDist.Web.Services;

/// <summary>Browser log forwarding settings (section <c>ClientLogging</c> of wwwroot/appsettings.json).</summary>
public sealed class ClientLoggingOptions
{
    public const string SectionName = "ClientLogging";

    /// <summary>Forward browser logs to the server (which writes them to LogServer).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Lowest level forwarded.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Warning;

    /// <summary>Flush interval.</summary>
    public int FlushSeconds { get; set; } = 5;

    /// <summary>Entries kept while the server cannot be reached (oldest dropped first).</summary>
    public int MaxQueue { get; set; } = 500;
}

/// <summary>In-memory queue shared by the logger provider and <see cref="ClientLogShipper"/>.</summary>
public sealed class ClientLogQueue(IOptions<ClientLoggingOptions> options)
{
    private readonly ConcurrentQueue<ClientLogEntry> _items = new();
    private readonly SemaphoreSlim _urgent = new(0, int.MaxValue);

    /// <summary>Options.</summary>
    public ClientLoggingOptions Options => options.Value;

    /// <summary>Queued entries.</summary>
    public int Count => _items.Count;

    /// <summary>Adds an entry; Error and Critical wake the shipper at once.</summary>
    public void Enqueue(ClientLogEntry entry, bool urgent)
    {
        _items.Enqueue(entry);
        while (_items.Count > Options.MaxQueue && _items.TryDequeue(out _))
        {
        }
        if (urgent || _items.Count >= 50)
        {
            _urgent.Release();
        }
    }

    /// <summary>Takes up to <paramref name="max"/> entries.</summary>
    public List<ClientLogEntry> Take(int max)
    {
        var list = new List<ClientLogEntry>(Math.Min(max, _items.Count));
        while (list.Count < max && _items.TryDequeue(out var e))
        {
            list.Add(e);
        }
        return list;
    }

    /// <summary>Puts entries back at the end (after a failed send).</summary>
    public void Requeue(IEnumerable<ClientLogEntry> entries)
    {
        foreach (var e in entries)
        {
            Enqueue(e, urgent: false);
        }
    }

    /// <summary>Waits for an urgent entry or the timeout.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _urgent.WaitAsync(timeout, ct);
}

/// <summary>
/// Logger provider that queues entries of this app and of WebCommon.UI (e.g. unhandled errors caught by AppErrorBoundary).
/// It never logs tokens: messages are formatted by the caller and the server masks secrets again.
/// </summary>
public sealed class ClientLogProvider(ClientLogQueue queue, NavigationManagerAccessor nav) : ILoggerProvider
{
    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        categoryName.StartsWith("JobDist.Web", StringComparison.Ordinal) || categoryName.StartsWith("WebCommon.UI", StringComparison.Ordinal)
            ? new ClientLogger(categoryName, queue, nav)
            : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class ClientLogger(string category, ClientLogQueue queue, NavigationManagerAccessor nav) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => queue.Options.Enabled && logLevel != LogLevel.None && logLevel >= queue.Options.MinimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            var message = formatter(state, exception);
            var entry = new ClientLogEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, LevelName(logLevel), Trim(category, 200), Trim(string.IsNullOrEmpty(message) ? "-" : message, 4_000),
                Trim(nav.CurrentPath, 500), exception is null ? null : Trim(exception.ToString(), 8_000));
            queue.Enqueue(entry, logLevel >= LogLevel.Error);
        }

        private static string LevelName(LogLevel level) => level switch
        {
            LogLevel.Trace or LogLevel.Debug => "Debug",
            LogLevel.Information => "Information",
            LogLevel.Warning => "Warning",
            LogLevel.Error => "Error",
            _ => "Critical"
        };

        private static string? Trim(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
    }
}

/// <summary>Current page path for log entries (set by <see cref="ClientLogShipper"/>; path only, never the query string).</summary>
public sealed class NavigationManagerAccessor
{
    /// <summary>Path such as <c>/jobs/…</c>.</summary>
    public string? CurrentPath { get; set; }
}

/// <summary>
/// Sends queued browser logs to <c>POST /api/v1/client-logs</c> in batches of up to 50: every <c>FlushSeconds</c>,
/// at once for Error / Critical, and only while signed in (the endpoint needs a token). Started by MainLayout.
/// </summary>
public sealed class ClientLogShipper(ClientLogQueue queue, NavigationManagerAccessor accessor, NavigationManager nav, JobDistApi api, TokenStore tokens) : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Starts the loop once.</summary>
    public void Start()
    {
        if (_loop is not null || !queue.Options.Enabled)
        {
            return;
        }
        accessor.CurrentPath = "/" + nav.ToBaseRelativePath(nav.Uri).Split('?', '#')[0];
        nav.LocationChanged += (_, e) => accessor.CurrentPath = "/" + nav.ToBaseRelativePath(e.Location).Split('?', '#')[0];
        _loop = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(queue.Options.FlushSeconds, 1, 300));
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Back off up to 1 minute while the server rejects or cannot be reached.
                await queue.WaitAsync(failures == 0 ? interval : TimeSpan.FromSeconds(Math.Min(60, interval.TotalSeconds * Math.Pow(2, failures))), ct);
                if (queue.Count == 0 || string.IsNullOrEmpty(await tokens.GetAsync()))
                {
                    continue;
                }
                var batch = queue.Take(50);
                if (await api.SendClientLogsAsync(new ClientLogBatchRequest(batch), ct))
                {
                    failures = 0;
                }
                else
                {
                    failures = Math.Min(failures + 1, 6);
                    queue.Requeue(batch);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}
