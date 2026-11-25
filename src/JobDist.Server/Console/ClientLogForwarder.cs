using System.Collections.Concurrent;
using JobDist.Contracts;

namespace JobDist.Server.Console;

/// <summary>Browser log settings (section <c>ClientLogging</c>).</summary>
public sealed class ClientLoggingOptions
{
    public const string SectionName = "ClientLogging";

    /// <summary>Accept browser logs.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Lowest level accepted from browsers.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
}

/// <summary>
/// Writes browser log entries through <see cref="ILogger"/> (category <c>JobDist.Web.{category}</c>) so Common.Logging ships
/// them to LogServer. Server-side facts (user id, client IP, user agent) are added here, never trusted from the browser.
/// Entries already received in the last 10 minutes (same <c>entryId</c>) are skipped, so a retried batch is not logged twice.
/// </summary>
public sealed class ClientLogForwarder(ILoggerFactory loggers, Microsoft.Extensions.Options.IOptionsMonitor<ClientLoggingOptions> options, TimeProvider time)
{
    private static readonly TimeSpan DedupWindow = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _seen = new();
    private long _writes;

    /// <summary>Logs a batch.</summary>
    public void Write(ClientLogBatchRequest batch, string userId, string? clientIp, string? userAgent)
    {
        var o = options.CurrentValue;
        if (!o.Enabled)
        {
            return;
        }
        var now = time.GetUtcNow();
        foreach (var e in batch.Entries)
        {
            if (!Enum.TryParse<LogLevel>(e.Level, out var level) || level < o.MinimumLevel)
            {
                continue;
            }
            if (e.EntryId != Guid.Empty && !_seen.TryAdd(e.EntryId, now))
            {
                continue;
            }
            var category = "JobDist.Web." + new string(e.Category.Where(c => char.IsLetterOrDigit(c) || c is '.' or '_').Take(150).ToArray());
            var logger = loggers.CreateLogger(category);
            using var scope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["UserId"] = userId, ["ClientIp"] = clientIp, ["UserAgent"] = Clip(userAgent, 300), ["Page"] = Clip(e.Page, 500),
                ["CorrelationId"] = Clip(e.CorrelationId, 100), ["ClientTimestampUtc"] = e.TimestampUtc, ["Source"] = "browser"
            });
            logger.Log(level, "{ClientMessage}{ClientException}", Clip(e.Message, 4_000), e.Exception is null ? string.Empty : "\n" + Clip(e.Exception, 8_000));
        }

        // Occasionally drop old de-duplication entries.
        if (Interlocked.Increment(ref _writes) % 100 == 0)
        {
            foreach (var kv in _seen.Where(kv => now - kv.Value > DedupWindow))
            {
                _seen.TryRemove(kv.Key, out _);
            }
        }
    }

    private static string? Clip(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
