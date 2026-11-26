using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace JobDist.Infrastructure.Http;

/// <summary>
/// Watches responses from services JobDist calls (AuthServer, MessageHub) for the version-retirement headers they send:
/// <c>Deprecation</c> (RFC 9745), <c>Sunset</c> (RFC 8594) and <c>Link: rel="successor-version"</c>. Logs one warning per
/// service and path at most once a day, so operators learn about a retirement before the old version starts answering 404/410.
/// </summary>
public sealed class UpstreamVersionMonitor(TimeProvider time, ILogger<UpstreamVersionMonitor> logger)
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastLogged = new(StringComparer.Ordinal);

    /// <summary>Checks one response.</summary>
    /// <param name="service">Service name for the log, e.g. <c>MessageHub</c>.</param>
    /// <param name="path">Request path (no query string).</param>
    /// <param name="headers">Response headers.</param>
    /// <returns><c>true</c> when the response announced a deprecation or sunset.</returns>
    public bool Observe(string service, string path, HttpResponseHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var deprecation = First(headers, "Deprecation");
        var sunset = First(headers, "Sunset");
        if (deprecation is null && sunset is null)
        {
            return false;
        }
        var key = service + " " + path;
        var now = time.GetUtcNow();
        if (_lastLogged.TryGetValue(key, out var last) && now - last < Interval)
        {
            return true;
        }
        _lastLogged[key] = now;
        var successor = headers.TryGetValues("Link", out var links)
            ? links.SelectMany(l => l.Split(',')).FirstOrDefault(l => l.Contains("successor-version", StringComparison.OrdinalIgnoreCase))?.Trim()
            : null;
        logger.LogWarning("{Service} reports that {Path} is deprecated (Deprecation: {Deprecation}, Sunset: {Sunset}, successor: {Successor}). Plan the upgrade before the sunset date.",
            service, path, deprecation ?? "-", sunset ?? "-", successor ?? "-");
        return true;
    }

    private static string? First(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
